﻿using CliWrap;
using EnvironmentServer.DAL;
using MySql.Data.MySqlClient;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EnvironmentServer.Daemon.Actions
{
    public class SnapshotCreate : ActionBase
    {
        public override string ActionIdentifier => "snapshot_create";

        public override async Task ExecuteAsync(Database db, long variableID, long userID)
        {
            //Get user, environment and databasename
            var user = db.Users.GetByID(userID);
            var snap = db.Snapshot.Get(variableID);
            var env = db.Environments.Get(snap.EnvironmentId);
            var dbString = user.Username + "_" + env.Name;

            //Stop Website (a2dissite)
            await Cli.Wrap("/bin/bash")
                .WithArguments($"-c \"a2dissite {user.Username}_{env.Name}.conf\"")
                .ExecuteAsync();
            await Cli.Wrap("/bin/bash")
                .WithArguments("-c \"service apache2 reload\"")
                .ExecuteAsync();

            //Create database dump in site folder
            await Cli.Wrap("/bin/bash")
                .WithArguments("-c \"mysqldump -u adm -p1594875!Adm " + dbString + " > db.sql\"")
                .WithWorkingDirectory($"/home/{user.Username}/files/{env.Name}")
                .ExecuteAsync();

            //check for git init
            await Cli.Wrap("/bin/bash")
                .WithArguments("-c \"git init\"")
                .WithWorkingDirectory($"/home/{user.Username}/files/{env.Name}")
                .ExecuteAsync();

            //git stage
            await Cli.Wrap("/bin/bash")
                .WithArguments("-c \"git stage -A\"")
                .WithWorkingDirectory($"/home/{user.Username}/files/{env.Name}")
                .ExecuteAsync();

            //create commit
            await Cli.Wrap("/bin/bash")
                .WithArguments("-c \"git commit\"")
                .WithWorkingDirectory($"/home/{user.Username}/files/{env.Name}")
                .ExecuteAsync();

            //save hash
            StringBuilder hash = new();
            await Cli.Wrap("/bin/bash")
                .WithArguments("-c \"git rev-parse head\"")
                .WithWorkingDirectory($"/home/{user.Username}/files/{env.Name}")
                .WithStandardOutputPipe(PipeTarget.ToStringBuilder(hash))
                .ExecuteAsync();

            using (var connection = db.GetConnection())
            {
                //SELECT * FROM environments_snapshots WHERE environments_Id_fk = 1 ORDER BY Created DESC LIMIT 1;
                var Command = new MySqlCommand($"UPDATE environments_snapshots SET Hash = {hash} WHERE id = {snap.Id};");
                Command.Connection = connection;
                Command.ExecuteNonQuery();
            }

            //restart site
            await Cli.Wrap("/bin/bash")
                .WithArguments($"-c \"a2ensite {user.Username}_{env.Name}.conf\"")
                .ExecuteAsync();
            await Cli.Wrap("/bin/bash")
                .WithArguments("-c \"service apache2 reload\"")
                .ExecuteAsync();
        }
    }
}
