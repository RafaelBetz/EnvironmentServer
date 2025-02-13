﻿using EnvironmentServer.DAL;
using EnvironmentServer.DAL.Models;
using EnvironmentServer.Interfaces;
using EnvironmentServer.Util;
using Microsoft.Extensions.DependencyInjection;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace EnvironmentServer.Daemon.Utility;

//This class is a cheesehad
internal static class EnvironmentPacker
{
    private const string PatternSW5Username = "('username' => ')(.*)(')";
    private const string PatternSW5Password = "('password' => ')(.*)(')";
    private const string PatternSW5DBName = "('dbname' => ')(.*)(')";

    public static async Task PackEnvironmentAsync(Database db, Environment env)
    {
        //Delete Cache
        var usr = db.Users.GetByID(env.UserID);
        PackerHelper.DeleteCache(usr.Username, env.InternalName);

        //Create Inactive Dir
        Directory.CreateDirectory($"/home/{usr.Username}/files/inactive");

        //Check if Environment.zip already exists and delete
        if (File.Exists($"/home/{usr.Username}/files/inactive/{env.InternalName}.zip"))
            File.Delete($"/home/{usr.Username}/files/inactive/{env.InternalName}.zip");

        //Zip Environment to inactive folder
        await Bash.CommandAsync($"zip -r /home/{usr.Username}/files/inactive/{env.InternalName}.zip {env.InternalName}",
            $"/home/{usr.Username}/files/");

        //Check for SW5 or SW6
        var sw6 = Directory.Exists($"/home/{usr.Username}/files/{env.InternalName}/public");

        //Delete Environment
        Directory.Delete($"/home/{usr.Username}/files/{env.InternalName}", true);

        //Create Redirection
        Directory.CreateDirectory($"/home/{usr.Username}/files/{env.InternalName}");

        var content = $@"<!DOCTYPE html>
                            <html>
                                <head>
                                    <meta http-equiv=""Refresh"" content=""0; url=https://cp.{db.Settings.Get("domain").Value}/Recover/{env.ID}"" />
                                </head> 
                            </html>";

        if (sw6)
        {
            Directory.CreateDirectory($"/home/{usr.Username}/files/{env.InternalName}/public");
            Directory.CreateDirectory($"/home/{usr.Username}/files/{env.InternalName}/public/admin");
        }
        else
        {
            Directory.CreateDirectory($"/home/{usr.Username}/files/{env.InternalName}/backend");
        }

        File.WriteAllText($"/home/{usr.Username}/files/{env.InternalName}/{(sw6 ? "public/" : "")}index.html",
                content);

        File.WriteAllText($"/home/{usr.Username}/files/{env.InternalName}/{(sw6 ? "public/admin/" : "backend/")}index.html",
                content);

        //Set Stored in DB
        db.Environments.SetStored(env.ID, true);

        //Set Privileges to user
        await Bash.ChownAsync(usr.Username, "sftp_users", $"/home/{usr.Username}/files/{env.InternalName}", recrusiv: true);
    }

    public static async Task UnpackEnvironmentAsync(ServiceProvider sp, Environment env)
    {
        var db = sp.GetService<Database>();
        var em = sp.GetService<IExternalMessaging>();
        var usr = db.Users.GetByID(env.UserID);

        //Check if Stored Environment exists
        if (!File.Exists($"/home/{usr.Username}/files/inactive/{env.InternalName}.zip"))
        {
            db.Logs.Add("Daemon", $"Restore Failed! File not found: /home/{usr.Username}/files/inactive/{env.InternalName}.zip");
            await em.SendMessageAsync($"Restore of Environment Failed! File not found: /home/{usr.Username}/files/inactive/{env.InternalName}.zip",
                db.UserInformation.Get(usr.ID).SlackID);
            return;
        }

        //Delete Redirection
        Directory.Delete($"/home/{usr.Username}/files/{env.InternalName}", true);

        //Unzip Environment
        await Bash.CommandAsync($"unzip /home/{usr.Username}/files/inactive/{env.InternalName}.zip", $"/home/{usr.Username}/files", validation: false);

        //Set Privileges to user
        await Bash.ChownAsync(usr.Username, "sftp_users", $"/home/{usr.Username}/files/{env.InternalName}", true);

        //Delete stored environment
        File.Delete($"/home/{usr.Username}/files/inactive/{env.InternalName}.zip");

        //Set Stored false in DB
        db.Environments.SetStored(env.ID, false);
    }

    public static async Task CreateTemplateAsync(Database db, Environment env, Template template)
    {
        var usr = db.Users.GetByID(env.UserID);

        db.Logs.Add("Daemon", $"Create Template for {usr.ID} - {usr.Username} - Template: {template.ID} - {template.Name}");

        //Disable Site
        await Bash.ApacheDisableSiteAsync($"{usr.Username}_{env.InternalName}.conf");
        await Bash.ReloadApacheAsync();

        //Delete Cache
        PackerHelper.DeleteCache(usr.Username, env.InternalName);

        //Dump DB to folder
        var dbString = usr.Username + "_" + env.InternalName;

        await Bash.CommandAsync($"mysqldump -u {dbString} -p{env.DBPassword} --hex-blob --default-character-set=utf8 " + dbString + " --result-file=db.sql",
            $"/home/{usr.Username}/files/{env.InternalName}");

        //Move Environment to tmp folder
        var templatePath = $"/root/templates/{template.ID}-{template.Name}";
        Directory.CreateDirectory(templatePath);

        var tmpPath = $"/root/templates/tmp/{usr.Username}/{template.Name}";
        Directory.CreateDirectory(tmpPath);

        await Bash.CommandAsync($"cp -a {env.InternalName}/. {tmpPath}",
            $"/home/{usr.Username}/files");

        //Enable Site
        await Bash.ApacheEnableSiteAsync($"{usr.Username}_{env.InternalName}.conf");
        await Bash.ReloadApacheAsync();

        //Replace parts in Config
        var sw6 = Directory.Exists($"{tmpPath}/public");

        if (sw6)
        {
            var cnf = File.ReadAllText(File.Exists($"{tmpPath}/.env.local") ? $"{tmpPath}/.env.local" : $"{tmpPath}/.env");
            var file = new IniFile(cnf);
            file.SetValue("APP_URL", "{{APPURL}}");
            file.SetValue("DATABASE_URL", "{{DATABASEURL}}");
            file.SetValue("SHOPWARE_ES_HOSTS", "localhost:9200");
            file.SetValue("SHOPWARE_ES_ENABLED", "0");
            File.WriteAllText(File.Exists($"{tmpPath}/.env.local") ? $"{tmpPath}/.env.local" : $"{tmpPath}/.env", file.Write());
        }
        else
        {
            var cnf = File.ReadAllText($"{tmpPath}/config.php");
            cnf = Regex.Replace(cnf, PatternSW5Username, "$1{{DBUSER}}$3");
            cnf = Regex.Replace(cnf, PatternSW5Password, "$1{{DBPASSWORD}}$3");
            cnf = Regex.Replace(cnf, PatternSW5DBName, "$1{{DBNAME}}$3");
            File.WriteAllText($"{tmpPath}/config.php", cnf);
        }

        //Replace parts in DB Dump
        var internalBin = Encoding.UTF8.GetBytes(env.InternalName);
        var usernameBin = Encoding.UTF8.GetBytes(usr.Username);
        var internalBinReplace = Encoding.UTF8.GetBytes("{{INTERNALNAME}}");
        var usernameBinReplace = Encoding.UTF8.GetBytes("{{USERNAME}}");

        var dbfile = File.ReadAllBytes($"{tmpPath}/db.sql");

        dbfile = PackerHelper.ReplaceBytesAll(dbfile, internalBin, internalBinReplace);
        dbfile = PackerHelper.ReplaceBytesAll(dbfile, usernameBin, usernameBinReplace);

        File.WriteAllBytes($"{tmpPath}/db-tmp.sql", dbfile);

        File.Delete($"{tmpPath}/db.sql");

        //Zip all to Template folder
        await Bash.CommandAsync($"zip -r {templatePath}/{template.Name}.zip .", $"{tmpPath}");

        //Remove tmp folder
        Directory.Delete($"{tmpPath}", true);

        db.Logs.Add("Daemon", $"Create Template successful for {usr.ID} - {usr.Username} - Template: {template.ID} - {template.Name}");
    }

    public static async Task DeployTemplateAsync(Database db, Environment env, long tmpID)
    {
        var template = db.Templates.Get(tmpID);
        var user = db.Users.GetByID(env.UserID);

        db.Logs.Add("Daemon", $"Template Deploy for {user.ID} - {user.Username} - Template: {template.ID} - {template.Name}");

        //Unzip template
        await Bash.CommandAsync($"unzip -o -q /root/templates/{template.ID}-{template.Name}/{template.Name}.zip",
            $"/home/{user.Username}/files/{env.InternalName}", validation: false);

        //Replace parts in config
        var sw6 = Directory.Exists($"/home/{user.Username}/files/{env.InternalName}/public");

        if (sw6)
        {
            var confPath = $"/home/{user.Username}/files/{env.InternalName}/.env.local";
            if (!File.Exists(confPath))
                confPath = $"/home/{user.Username}/files/{env.InternalName}/.env";

            var cnf = File.ReadAllText(confPath);
            cnf = cnf.Replace("{{APPURL}}", $"https://{env.InternalName}-{user.Username}.{db.Settings.Get("domain").Value}");
            cnf = cnf.Replace("{{DATABASEURL}}",
                $"mysql://{user.Username}_{env.InternalName}:{env.DBPassword}@localhost:3306/{user.Username}_{env.InternalName}");
            cnf = cnf.Replace("{{COMPOSER}}", $"/home/{user.Username}/files/{env.InternalName}/var/cache/composer");
            File.WriteAllText(confPath, cnf);
        }
        else
        {
            var cnf = File.ReadAllText($"/home/{user.Username}/files/{env.InternalName}/config.php");
            cnf = cnf.Replace("{{DBUSER}}", $"{user.Username}_{env.InternalName}");
            cnf = cnf.Replace("{{DBPASSWORD}}", env.DBPassword);
            cnf = cnf.Replace("{{DBNAME}}", $"{user.Username}_{env.InternalName}");
            File.WriteAllText($"/home/{user.Username}/files/{env.InternalName}/config.php", cnf);
        }

        //Replace parts in DB Dump
        var internalBin = Encoding.UTF8.GetBytes(env.InternalName);
        var usernameBin = Encoding.UTF8.GetBytes(user.Username);
        var internalBinReplace = Encoding.UTF8.GetBytes("{{INTERNALNAME}}");
        var usernameBinReplace = Encoding.UTF8.GetBytes("{{USERNAME}}");

        var dbfile = File.ReadAllBytes($"/home/{user.Username}/files/{env.InternalName}/db-tmp.sql");

        dbfile = PackerHelper.ReplaceBytesAll(dbfile, internalBinReplace, internalBin);
        dbfile = PackerHelper.ReplaceBytesAll(dbfile, usernameBinReplace, usernameBin);

        File.WriteAllBytes($"/home/{user.Username}/files/{env.InternalName}/db.sql", dbfile);

        File.Delete($"/home/{user.Username}/files/{env.InternalName}/db-tmp.sql");

        //Set Privileges to user
        await Bash.ChownAsync(user.Username, "sftp_users", $"/home/{user.Username}/files/{env.InternalName}", recrusiv: true);

        //Import DB
        await Bash.CommandAsync($"mysql -u {user.Username}_{env.InternalName} -p{env.DBPassword} {user.Username}_{env.InternalName} < db.sql",
            $"/home/{user.Username}/files/{env.InternalName}");

        db.Logs.Add("Daemon", $"Template Deploy sucessful for {user.ID} - {user.Username} - Template: {template.ID} - {template.Name}");
    }

    public static void DeleteTemplate(Database db, long tmpID)
    {
        var template = db.Templates.Get(tmpID);

        db.Templates.Delete(tmpID);

        //Delete Template file
        Directory.Delete($"/root/templates/{template.ID}-{template.Name}", true);
    }
}