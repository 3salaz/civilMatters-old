using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace FullscreenEditor {
    public static class Cmd {

        public static string Run(string command, params object[] formatArgs) {
            return Run(command, false, formatArgs);
        }

        public static string Run(string command, bool asAdmin, params object[] formatArgs) {
            var proc = new Process();
            var stdout = new StringBuilder();

            command = string.Format(command, formatArgs);

            proc.EnableRaisingEvents = true;

            if (Application.platform == RuntimePlatform.WindowsEditor)
                proc.StartInfo = new ProcessStartInfo() {
                    FileName = "cmd.exe",
                    Arguments = "/C \"" + command + "\"",
                    UseShellExecute = asAdmin,
                    RedirectStandardError = !asAdmin,
                    RedirectStandardOutput = !asAdmin,
                    Verb = asAdmin? "runas": "",
                    CreateNoWindow = !asAdmin,
                    WorkingDirectory = Environment.CurrentDirectory
                };
            else
                proc.StartInfo = new ProcessStartInfo() {
                    FileName = "/bin/bash",
                    Arguments = "-c \"" + command + "\"",
                    UseShellExecute = asAdmin,
                    RedirectStandardError = !asAdmin,
                    RedirectStandardOutput = !asAdmin,
                    CreateNoWindow = !asAdmin,
                    WorkingDirectory = Environment.CurrentDirectory
                };

            if (!asAdmin) {
                proc.OutputDataReceived += (sender, args) => {
                    if (!string.IsNullOrEmpty(args.Data))
                        stdout.AppendLine(args.Data);
                };

                proc.ErrorDataReceived += (sender, args) => {
                    if (!string.IsNullOrEmpty(args.Data))
                        stdout.AppendLine(args.Data);
                };
            }

            //proc.Exited += (sender, args) => {
            //    Debug.LogWarningFormat("Command {0} exited with code {1}", command, proc.ExitCode);
            //};

            if (proc.Start()) {
                if (!asAdmin) {
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();
                }
                proc.WaitForExit();

                if (!string.IsNullOrEmpty(stdout.ToString()))
                    Debug.Log(stdout);

                if (proc.ExitCode == 0)
                    return stdout.ToString().Trim();

                throw new Exception(string.Format("Command {0} exited with code {1}", command, proc.ExitCode));
            }

            throw new Exception(string.Format("Failed to start process for {0}", command));
        }

    }
}
