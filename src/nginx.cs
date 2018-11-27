using System;
using System.Collections;
using System.Collections.Generic;
using System.Configuration.Install;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Threading;

class NginxD : ServiceBase
{
    class NginxDaemon
    {
        /// <summary>
        /// run nginx 
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        static public Process Run(string args)
        {
            ProcessStartInfo si = new ProcessStartInfo();
            si.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
            si.FileName = "nginx.exe";
            si.Arguments = args;
            si.UseShellExecute = false;
            si.RedirectStandardError = true;
            si.RedirectStandardOutput = true;

            return Process.Start(si);
        }

        private bool running = false;
        private readonly ServiceBase service;

        public NginxDaemon(ServiceBase service)
        {
            this.service = service;
        }

        public void Start()
        {
            CheckConfig();  // ensure configuration is OK

            running = true;
            Run("-s quit").WaitForExit(); // clean up previous nginx process
            new Thread(RunNginx).Start();
        }

        void CheckConfig()
        {
            var check = Run("-t");
            check.WaitForExit();
            if (check.ExitCode != 0)
            {
                throw new InvalidOperationException(check.StandardError.ReadToEnd());
            }
        }

        void RunNginx()
        {
            while (running)
            {
                try
                {
                    var nginx = Run(string.Empty);
                    if (nginx.HasExited) throw new InvalidOperationException(nginx.StandardError.ReadToEnd());
                    nginx.WaitForExit();
                    if (nginx.ExitCode == 0) // safe quit : maybe nginx -s quit or nginx -s stop
                    {
                        service.Stop();
                        break;
                    }
                }
                catch (Exception)
                {
                    Thread.Sleep(500);
                }
            }
        }

        public void Stop()
        {
            running = false;
            Run("-s stop").WaitForExit();
        }
    }

    class NginxControlServer
    {
        NamedPipeServerStream serverStream = ControlPipeFactory.CreateServer("instance");

        public void Open()
        {
            serverStream.BeginWaitForConnection(OnConnectionEstablished, null);
        }

        void OnConnectionEstablished(IAsyncResult asr)
        {
            try
            {
                serverStream.EndWaitForConnection(asr);
                try
                {
                    var args = new StreamReader(serverStream).ReadLine();
                    var command = NginxDaemon.Run(args);
                    var writer = new StreamWriter(serverStream);
                    writer.Write(command.StandardOutput.ReadToEnd());
                    writer.Write(command.StandardError.ReadToEnd());
                    writer.Flush();
                    serverStream.WaitForPipeDrain();
                }
                finally
                {
                    serverStream.Disconnect();
                    serverStream.BeginWaitForConnection(OnConnectionEstablished, null);
                }
            }
            catch (ObjectDisposedException) // shutdown
            {
            }
        }

        public void Close()
        {
            serverStream.Dispose();
        }
    }

    class NginxControlCommand
    {
        private readonly string args;

        public NginxControlCommand(string[] args)
        {
            this.args = string.Join(" ", args);
        }

        public void Execute()
        {
            var client = ControlPipeFactory.CreateClient("instance");
            try
            {
                client.Connect(1);
                var writer = new StreamWriter(client);
                writer.WriteLine(string.IsNullOrEmpty(args) ? "-?" : args);
                writer.Flush();
                client.WaitForPipeDrain();
                Console.Write(new StreamReader(client).ReadToEnd());
            }
            catch (System.TimeoutException)
            {
                Console.Error.Write("failed to connect nginxd service .");
            }
        }
    }

    static class ControlPipeFactory
    {
        static string GetPipeName(string type)
        {
            var path = typeof(ControlPipeFactory).Assembly.CodeBase;
            using (var md5 = MD5.Create())
            {
                var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(path));
                return "nginx/" + type + "/" + new Guid(hash).ToString("n");
            }
        }

        /// <summary>
        /// create server pipe stream
        /// </summary>
        /// <returns></returns>
        static public NamedPipeServerStream CreateServer(string type)
        {
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule("Users", PipeAccessRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            var name = GetPipeName(type);
            return new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Message, PipeOptions.Asynchronous, 0, 0, security);
        }

        /// <summary>
        /// create client pipe stream
        /// </summary>
        /// <returns></returns>
        static public NamedPipeClientStream CreateClient(string type)
        {
            var name = GetPipeName(type);
            return new NamedPipeClientStream(".", name);
        }
    }

    static class ServiceController
    {
        static ServiceController()
        {
            var user = WindowsIdentity.GetCurrent();
            const string PipeType = "controller";

            if (!new WindowsPrincipal(user).IsInRole(WindowsBuiltInRole.Administrator))
            {
                ProcessStartInfo si = new ProcessStartInfo();
                si.WindowStyle = ProcessWindowStyle.Hidden;
                si.UseShellExecute = true;
                si.FileName = typeof(ServiceController).Assembly.Location;
                si.WorkingDirectory = Environment.CurrentDirectory;
                si.Arguments = Environment.CommandLine.Substring(Environment.CommandLine.IndexOf(' ') + 1);
                si.Verb = "runas";

                var pipe = ControlPipeFactory.CreateServer(PipeType);
                pipe.BeginWaitForConnection(OnPipeConnected, pipe);

                try
                {
                    invoke = Process.Start(si);
                }
                catch
                {
                    Console.WriteLine("Install/Remove service requires administrator priviledge.");
                    Environment.Exit(1);
                }
            }
            else
            {
                var pipe = ControlPipeFactory.CreateClient(PipeType);
                try
                {
                    pipe.Connect(1);
                    writer = new StreamWriter(pipe) { AutoFlush = true };
                }
                catch (System.TimeoutException)
                {
                    writer = Console.Out;
                }
                Debugger.Launch();
            }
        }

        static readonly TextWriter writer;

        static readonly Process invoke;

        static void OnPipeConnected(IAsyncResult ar)
        {
            var pipe = (NamedPipeServerStream)ar.AsyncState;
            pipe.EndWaitForConnection(ar);

            using (var reader = new StreamReader(pipe))
            {
                while (!reader.EndOfStream)
                {
                    Console.WriteLine(reader.ReadLine());
                }
            }
        }

        static void InvokeRunAs(Action<IDictionary, ServiceProcessInstaller, ServiceInstaller> action)
        {
            if (invoke != null)
            {
                invoke.WaitForExit();
                Environment.Exit(invoke.ExitCode);
            }
            else
            {
                var state = new Hashtable();

                using (var installer = new ServiceProcessInstaller())
                using (var serviceInstaller = new ServiceInstaller())
                {
                    try
                    {
                        installer.Account = ServiceAccount.NetworkService;
                        installer.Installers.Add(serviceInstaller);
                        action(state, installer, serviceInstaller);
                        if (state.Count > 0) installer.Commit(state);
                        writer.Write("Success.");
                    }
                    catch (Exception e)
                    {
                        if (state.Count > 0) installer.Rollback(state);
                        writer.WriteLine("Error: " + e.Message);
                    }
                }
            }
        }

        static public void Install(string serviceName, string displayName, string description)
        {
            InvokeRunAs((state, installer, serviceInstaller) =>
            {
                writer.WriteLine("Install service " + serviceName);

                installer.Context = serviceInstaller.Context = new InstallContext(null, null);
                serviceInstaller.Context.Parameters.Add("assemblyPath", typeof(ServiceController).Assembly.Location + " " + serviceName);

                serviceInstaller.ServiceName = serviceName;
                serviceInstaller.Description = displayName ?? "nginx daemon";
                serviceInstaller.DisplayName = description ?? "nginx service";
                serviceInstaller.StartType = ServiceStartMode.Automatic;

                installer.Install(state);
            });
        }

        static public void Remove(string serviceName)
        {
            InvokeRunAs((state, installer, serviceInstaller) =>
            {
                installer.Context = serviceInstaller.Context = new InstallContext(null, null);
                writer.WriteLine("Remove service " + serviceName);
                serviceInstaller.ServiceName = serviceName;
                installer.Uninstall(null);
            });
        }
    }

    NginxDaemon daemon;
    NginxControlServer control = new NginxControlServer();

    public NginxD()
    {
        ServiceName = "nginx";
        daemon = new NginxDaemon(this);
    }

    protected override void OnStart(string[] args)
    {
        daemon.Start();
        control.Open();
    }

    protected override void OnStop()
    {
        control.Close();
        daemon.Stop();
    }

    static void Main(string[] args)
    {
        if (Environment.UserInteractive)
        {
            if (args.Length > 0)
            {
                if (args[0] == "--install")
                {
                    ServiceController.Install(args.Length > 1 ? args[1] : "nginx"
                        , args.Length > 2 ? args[2] : null
                        , args.Length > 3 ? args[3] : null);
                    return;
                }
                else if (args[0] == "--remove")
                {
                    ServiceController.Remove(args.Length > 1 ? args[1] : "nginx");
                    return;
                }
            }
            new NginxControlCommand(args).Execute();
        }
        else
        {
            Run(new NginxD());
        }
    }
}
