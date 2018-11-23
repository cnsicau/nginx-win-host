using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
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
            si.FileName = @"nginx.exe";
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
            ThreadPool.UnsafeQueueUserWorkItem(RunNginx, null);
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

        void RunNginx(object state)
        {
            while (running)
            {
                try
                {
                    var nginx = Run(string.Empty);
                    if (nginx.HasExited) throw new InvalidOperationException(nginx.StandardError.ReadToEnd());
                    nginx.WaitForExit();
                    if (nginx.ExitCode == 0)
                    {
                        service.Stop(); // 安全退出服务
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
        NamedPipeServerStream serverStream = ControlPipeFactory.CreateServer();

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
            var client = ControlPipeFactory.CreateClient();
            try
            {
                client.Connect(1000);
                var writer = new StreamWriter(client);
                writer.WriteLine(string.IsNullOrEmpty(args) ? "-?" : args);
                writer.Flush();
                client.WaitForPipeDrain();
                Console.WriteLine(new StreamReader(client).ReadToEnd());
            }
            catch (System.TimeoutException)
            {
                Console.Error.WriteLine("failed to connect nginxd service .");
            }
        }
    }

    class ControlPipeFactory
    {
        static string GetPipeName()
        {
            var path = typeof(ControlPipeFactory).Assembly.CodeBase;
            using (var md5 = MD5.Create())
            {
                var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(path));
                return "nginxd/" + new Guid(hash).ToString("n");
            }
        }

        /// <summary>
        /// create server pipe stream
        /// </summary>
        /// <returns></returns>
        static public NamedPipeServerStream CreateServer()
        {
            var name = GetPipeName();
            var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Message, PipeOptions.Asynchronous);
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule("Users", PipeAccessRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            server.SetAccessControl(security);
            return server;
        }

        /// <summary>
        /// create client pipe stream
        /// </summary>
        /// <returns></returns>
        static public NamedPipeClientStream CreateClient()
        {
            var name = GetPipeName();
            return new NamedPipeClientStream(".", name);
        }
    }


    NginxDaemon daemon;
    NginxControlServer control = new NginxControlServer();

    public NginxD()
    {
        ServiceName = "nginxd";
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
            new NginxControlCommand(args).Execute();
        }
        else
        {
            Run(new NginxD());
        }
    }
}
