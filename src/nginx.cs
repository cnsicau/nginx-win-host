using System;
using System.Diagnostics;
using System.ServiceProcess;

class nginx : ServiceBase
{
    nginx()
    {
        ServiceName = "nginx";
        CanPauseAndContinue = true;
    }

    protected override void OnPause() { ExecuteNginxCommand("-s reopen"); }

    protected override void OnContinue() { ExecuteNginxCommand("-s reload"); }

    protected override void OnStart(string[] args) { ExecuteNginxCommand(string.Empty); }

    protected override void OnStop() { ExecuteNginxCommand("-s stop"); }

    static void Main() { Run(new nginx()); }

    void ExecuteNginxCommand(string args)
    {
        ProcessStartInfo si = new ProcessStartInfo();
        si.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
        si.FileName = "nginx.exe";
        si.Arguments = args;
        si.UseShellExecute = false;
        si.RedirectStandardError = true;
        si.RedirectStandardOutput = true;

        Process process = Process.Start(si);
        if(process.WaitForExit(1000))
            EventLog.WriteEntry("nginx " + args,
                "exit code: " + process.ExitCode +
                process.StandardOutput.ReadToEnd() + "\n" +
                process.StandardError.ReadToEnd()
            , process.ExitCode == 0 ? EventLogEntryType.Information : EventLogEntryType.Error);
    }
}