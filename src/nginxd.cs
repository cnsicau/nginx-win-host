using System;
using System.Collections;
using System.Collections.Generic;
using System.Configuration.Install;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

class NginxD : ServiceBase
{
    class NginxDaemon
    {
        private readonly NamedPipeServerStream commandStream = ControlPipeFactory.CreateServer("instance");
        private readonly LogRotaterDaemon rotater = new LogRotaterDaemon("logrotate");
        private readonly ServiceBase service;

        public NginxDaemon(ServiceBase service)
        {
            this.service = service;
        }

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

        public void Start()
        {
            CheckConfig();                  // ensure configuration is OK
            Run("-s quit").WaitForExit();   // clean up previous nginx process
            new Thread(RunNginx).Start();
            rotater.Start();
            commandStream.BeginWaitForConnection(OnReceiveCommand, null);
        }

        void OnReceiveCommand(IAsyncResult asr)
        {
            try
            {
                commandStream.EndWaitForConnection(asr);
                try
                {
                    var args = new StreamReader(commandStream).ReadLine();
                    var writer = new StreamWriter(commandStream) { AutoFlush = true };
                    if (args == "reconfig")
                    {
                        try
                        {
                            var options = rotater.Reconfig();
                            writer.WriteLine("reconfig " + options.Length + " log rotaters.");
                            foreach (var opt in options)
                            {
                                writer.WriteLine(" " + opt.Root + "\\" + opt.Filter + " { ");
                                writer.WriteLine("   " + opt.RotateType.ToString().ToLower() + " " + opt.RotateArguments);
                                writer.WriteLine("   rotate " + opt.Rotate);
                                writer.WriteLine("   compress " + (opt.Compress ? "on" : "off"));
                                writer.WriteLine("   delaycompress " + opt.DelayCompress);
                                if (opt.IncludeSubDirs) writer.WriteLine("   includesubdirs");
                                writer.WriteLine(" }");
                            }

                        }
                        catch (Exception e)
                        {
                            writer.Write("reconfig failed : " + e.Message);
                        }
                    }
                    else
                    {
                        var command = Run(args);
                        writer.Write(command.StandardOutput.ReadToEnd());
                        writer.Write(command.StandardError.ReadToEnd());
                    }
                    commandStream.WaitForPipeDrain();
                }
                finally
                {
                    commandStream.Disconnect();
                    commandStream.BeginWaitForConnection(OnReceiveCommand, null);
                }
            }
            catch (ObjectDisposedException) { } // shutdown
        }

        void CheckConfig()
        {
            var check = Run("-t");
            check.WaitForExit();
            if (check.ExitCode != 0) throw new InvalidOperationException(check.StandardError.ReadToEnd());
        }

        void RunNginx()
        {
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    var nginx = Run(string.Empty);
                    if (nginx.HasExited) throw new InvalidOperationException(nginx.StandardError.ReadToEnd());
                    nginx.WaitForExit();
                    if (nginx.ExitCode == 0) break; // safe quit : maybe nginx -s quit or nginx -s stop
                }
                catch (Exception)
                {
                    Thread.Sleep(1000);
                }
            }
            service.Stop();
        }

        public void Stop()
        {
            commandStream.Dispose();
            rotater.Stop();
            Run("-s stop").WaitForExit();
        }
    }

    static class NginxDaemonCommand
    {
        public static void Send(string args)
        {
            var client = ControlPipeFactory.CreateClient("instance");
            try
            {
                client.Connect(1);
                var writer = new StreamWriter(client) { AutoFlush = true };
                writer.WriteLine(string.IsNullOrEmpty(args) ? "-?" : args);
                client.WaitForPipeDrain();
                Console.Write(new StreamReader(client).ReadToEnd());
            }
            catch (System.TimeoutException)
            {
                Console.Error.Write("connect nginx daemon service timeout.");
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

        static public NamedPipeServerStream CreateServer(string type)
        {
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule("Users", PipeAccessRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            var name = GetPipeName(type);
            return new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Message, PipeOptions.Asynchronous, 0, 0, security);
        }

        static public NamedPipeClientStream CreateClient(string type)
        {
            var name = GetPipeName(type);
            return new NamedPipeClientStream(".", name);
        }
    }

    static class ServiceManager
    {
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
        delegate void InvokeAction(TextWriter writer, IDictionary state, ServiceProcessInstaller installer, ServiceInstaller serviceInstaller, object args);
        static void Invoke(InvokeAction action, object args)
        {
            const string PipeType = "controller";

            var user = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(user).IsInRole(WindowsBuiltInRole.Administrator))
            {
                ProcessStartInfo si = new ProcessStartInfo();
                si.WindowStyle = ProcessWindowStyle.Hidden;
                si.UseShellExecute = true;
                si.FileName = typeof(ServiceManager).Assembly.Location;
                si.WorkingDirectory = Environment.CurrentDirectory;
                si.Arguments = Environment.CommandLine.Substring(Environment.CommandLine.IndexOf(' ') + 1);
                si.Verb = "runas";

                using (var server = ControlPipeFactory.CreateServer(PipeType))
                {
                    server.BeginWaitForConnection(OnPipeConnected, server);

                    try
                    {
                        Process.Start(si).WaitForExit();
                    }
                    catch
                    {
                        Console.WriteLine("Install/Uninstall service requires administrator priviledge.");
                        Environment.Exit(1);
                    }
                }
            }
            else
            {
                var pipe = ControlPipeFactory.CreateClient(PipeType);
                TextWriter writer;
                try
                {
                    pipe.Connect(1);
                    writer = new StreamWriter(pipe) { AutoFlush = true };
                }
                catch (System.TimeoutException)
                {
                    writer = Console.Out;
                }

                var state = new Hashtable();

                using (var installer = new ServiceProcessInstaller())
                using (var serviceInstaller = new ServiceInstaller())
                {
                    try
                    {
                        installer.Account = ServiceAccount.LocalSystem;
                        installer.Installers.Add(serviceInstaller);
                        action(writer, state, installer, serviceInstaller, args);
                        if (state.Count > 0) installer.Commit(state);
                        writer.Write("Success.");
                    }
                    catch (Exception e)
                    {
                        if (state.Count > 0) installer.Rollback(state);
                        writer.WriteLine("Error: " + e.Message);
                        Environment.Exit(1);
                    }
                }
            }
            Environment.Exit(0);
        }

        static public void Install(string serviceName, string displayName, string description)
        {
            Invoke(Install, new string[] { serviceName, displayName, description });
        }

        static bool IsNetFramework2()
        {
            return typeof(ServiceInstaller).Assembly.ImageRuntimeVersion.CompareTo("v4") == -1;
        }

        static void Install(TextWriter writer, IDictionary state, ServiceProcessInstaller installer, ServiceInstaller serviceInstaller, object args)
        {
            var arr = (string[])args;
            writer.WriteLine("Install service " + arr[0]);

            installer.Context = serviceInstaller.Context = new InstallContext(null, null);
            serviceInstaller.Context.Parameters.Add("assemblyPath", IsNetFramework2()
                ? (typeof(ServiceManager).Assembly.Location + "\" \"" + arr[0])
                : ("\"" + typeof(ServiceManager).Assembly.Location + "\" " + arr[0]));

            serviceInstaller.ServiceName = arr[0];
            serviceInstaller.DisplayName = arr[1] ?? "Nginx Daemon";
            serviceInstaller.Description = arr[2] ?? "Nginx daemon service & Tiny log rotate provider.";
            serviceInstaller.StartType = ServiceStartMode.Automatic;

            installer.Install(state);
        }

        static public void Uninstall(string serviceName)
        {
            Invoke(Uninstall, serviceName);
        }

        static void Uninstall(TextWriter writer, IDictionary state, ServiceProcessInstaller installer, ServiceInstaller serviceInstaller, object args)
        {
            installer.Context = serviceInstaller.Context = new InstallContext(null, null);
            writer.WriteLine("Uninstall service " + (string)args);
            serviceInstaller.ServiceName = (string)args;
            installer.Uninstall(null);
        }

        static public void Start(string serviceName)
        {
            Invoke(Start, serviceName);
        }
        static void Start(TextWriter writer, IDictionary state, ServiceProcessInstaller installer, ServiceInstaller serviceInstaller, object args)
        {
            writer.WriteLine("Start service " + (string)args);
            var service = new ServiceController((string)args);
            if (service.Status != ServiceControllerStatus.Running) service.Start();

            WaitForStatus(service, ServiceControllerStatus.Running);
        }

        static public void Stop(string serviceName)
        {
            Invoke(Stop, serviceName);
        }
        static void Stop(TextWriter writer, IDictionary state, ServiceProcessInstaller installer, ServiceInstaller serviceInstaller, object args)
        {
            writer.WriteLine("Stop service " + (string)args);
            var service = new ServiceController((string)args);
            if (service.Status != ServiceControllerStatus.Stopped) service.Stop();

            WaitForStatus(service, ServiceControllerStatus.Stopped);
        }

        static void WaitForStatus(ServiceController service, ServiceControllerStatus status)
        {
            service.WaitForStatus(status, TimeSpan.FromSeconds(30));

            if (service.Status != status) throw new InvalidOperationException("Invalid service status " + service.Status);
        }
    }


    public enum RotateType
    {
        Daily = 0
    }

    public class LogRotateOptions
    {
        /// <summary>root directory, like logs\</summary>
        public string Root { get; set; }

        /// <summary>file filter, like *.logs
        /// </summary>
        public string Filter { get; set; }

        public RotateType RotateType { get; set; }

        /// <summary>rotate type parameter, 1:00:00</summary>
        public string RotateArguments { get; set; }

        /// <summary>rotate days</summary>
        public int Rotate { get; set; }

        public bool Compress { get; set; }

        /// <summary>compress delay days</summary>
        public int DelayCompress { get; set; }

        /// <summary>include sub directories</summary>
        public bool IncludeSubDirs { get; set; }
    }

    public class LogRotateOptionsBuilder
    {
        private readonly Dictionary<string, string> parameters = new Dictionary<string, string>(StringComparer.CurrentCultureIgnoreCase);
        private readonly string root;

        public LogRotateOptionsBuilder(string root) { this.root = root; }

        public LogRotateOptionsBuilder AddBuildParameter(string name, string value)
        {
            parameters[name] = value;

            return this;
        }

        public virtual LogRotateOptions Build()
        {
            var options = new LogRotateOptions();

            BuildRotateType(options);
            BuildRotate(options);
            BuildCompress(options);
            BuildDelayCompress(options);
            BuildIncludeSubDirs(options);

            BuildRootFilter(options);
            return options;
        }

        private void BuildRotateType(LogRotateOptions options)
        {
            foreach (var name in Enum.GetNames(typeof(RotateType)))
            {
                string arguments;
                if (parameters.TryGetValue(name, out arguments))
                {
                    options.RotateType = (RotateType)Enum.Parse(typeof(RotateType), name);
                    options.RotateArguments = arguments;
                }
            }
        }

        private void BuildRotate(LogRotateOptions options)
        {
            string rotate;
            if (parameters.TryGetValue("rotate", out rotate))
            {
                int days;
                if (!int.TryParse(rotate, out days))
                {
                    throw new InvalidOperationException("invalid rotate value " + rotate);
                }
                options.Rotate = days;
            }
            else
            {
                options.Rotate = 90;
            }
        }

        private void BuildCompress(LogRotateOptions options)
        {
            string compress;
            options.Compress = true;
            if (parameters.TryGetValue("compress", out compress))
            {
                if (compress == "off") options.Compress = false;
                else if (!string.IsNullOrEmpty(compress) && compress != "on")
                    throw new InvalidOperationException("invalid compress value " + compress);
            }
        }

        private void BuildIncludeSubDirs(LogRotateOptions options)
        {
            options.IncludeSubDirs = parameters.ContainsKey("includesubdirs");
        }

        private void BuildDelayCompress(LogRotateOptions options)
        {
            string delaycompress;
            int days = 1;
            if (parameters.TryGetValue("delaycompress", out delaycompress))
            {
                if (!string.IsNullOrEmpty(delaycompress)
                    && !int.TryParse(delaycompress, out days))
                    throw new InvalidOperationException("invalid delaycompress value " + delaycompress);

                if (days <= 0)
                    throw new InvalidOperationException("delaycompress must great than 0.");
            }
            options.DelayCompress = days;
        }

        private void BuildRootFilter(LogRotateOptions options)
        {
            options.Root = Path.GetDirectoryName(root);
            options.Filter = Path.GetFileName(root);

            if (string.IsNullOrEmpty(options.Root))
                options.Root = "logs";

            if (string.IsNullOrEmpty(options.Filter))
                options.Filter = "*.log";
        }
    }

    public class FileLogRotateOptionsBuilderProvider
    {
        private readonly string file;

        class DefaultLogRotateOptionsBuilder : LogRotateOptionsBuilder
        {
            public DefaultLogRotateOptionsBuilder() : base(string.Empty) { }

            public override LogRotateOptions Build()
            {
                return new LogRotateOptions
                {
                    Compress = true,
                    DelayCompress = 1,
                    Filter = "*.log",
                    Root = @"logs\",
                    IncludeSubDirs = true,
                    Rotate = 90,
                    RotateArguments = "0:00:00",
                    RotateType = RotateType.Daily
                };
            }
        }

        public FileLogRotateOptionsBuilderProvider(string file)
        {
            this.file = file;
        }

        public LogRotateOptionsBuilder CreateBuilder()
        {
            var file = this.file;

            if (string.IsNullOrEmpty(file)) return new DefaultLogRotateOptionsBuilder();

            if (!File.Exists(file))
            {
                file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, file);
                if (!File.Exists(file)) return null;
            }

            using (var stream = File.OpenRead(file))
            {
                var reader = new StreamReader(stream);
                string line = reader.ReadLine();
                while (IsCommentOrEmpty(line)) line = reader.ReadLine();

                LogRotateOptionsBuilder builder = ParseBuilder(reader, line);

                while (!reader.EndOfStream)
                {
                    line = reader.ReadLine();
                    if (!IsCommentOrEmpty(line))
                        throw new InvalidOperationException("config out of { - } : " + line);
                }

                return builder;
            }
        }

        private LogRotateOptionsBuilder ParseBuilder(StreamReader reader, string line)
        {
            var root = ReadRootLine(line);
            var builder = new LogRotateOptionsBuilder(root);
            bool eof = false;
            while (!eof && !reader.EndOfStream)
            {
                line = reader.ReadLine();
                eof = Regex.IsMatch(line, @"^\s*}\s*$");
                if (!eof && !IsCommentOrEmpty(line))
                {
                    EmitBuildParameter(builder, line);
                }
            }

            if (!eof) throw new InvalidOperationException("missing eof }");
            return builder;
        }

        public LogRotateOptionsBuilder[] CreateBuilders()
        {
            var builders = new List<LogRotateOptionsBuilder>();
            var file = this.file;

            if (string.IsNullOrEmpty(file)) return new LogRotateOptionsBuilder[0];

            if (!File.Exists(file))
            {
                file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, file);
                if (!File.Exists(file)) return new LogRotateOptionsBuilder[0];
            }

            using (var stream = File.OpenRead(file))
            {
                var reader = new StreamReader(stream);
                while (!reader.EndOfStream)
                {
                    string line = reader.ReadLine();
                    if (IsCommentOrEmpty(line)) continue;

                    builders.Add(ParseBuilder(reader, line));
                }

                return builders.ToArray();
            }
        }

        string ReadRootLine(string line)
        {
            var match = Regex.Match(line, @"^\s*(?<root>.*?)\s*\{\s*$");
            if (!match.Success) throw new InvalidOperationException("invalid root " + line);
            return match.Groups["root"].Value;
        }

        void EmitBuildParameter(LogRotateOptionsBuilder builder, string line)
        {
            var match = Regex.Match(line, @"^\s*(?<name>\S+)(\s+(?<val>.*?))?\s*$");
            if (!match.Success)
                throw new InvalidOperationException("invalid config " + line);
            var name = match.Groups["name"].Value;
            var value = match.Groups["val"].Value.TrimEnd();
            builder.AddBuildParameter(name, string.IsNullOrEmpty(value) ? null : value);
        }

        bool IsCommentOrEmpty(string line) { return Regex.IsMatch(line, @"^\s*(#|\s*$)"); }
    }

    public class LogRotaterDaemon
    {
        private readonly string optionsFile;
        private LogRotater[] rotaters;
        private Timer timer;
        private int tickSecond = -1;

        public LogRotaterDaemon(string optionsFile)
        {
            this.optionsFile = optionsFile;
        }

        public void Start()
        {
            Reconfig();
            this.timer = new Timer(OnTick, null, 750, 750);
        }

        void OnTick(object state)
        {
            var now = DateTime.Now;
            if (tickSecond == now.Second) return;
            tickSecond = now.Second;

            ThreadPool.UnsafeQueueUserWorkItem(ExecuteRotater, now);

        }

        void ExecuteRotater(object state)
        {
            var now = (DateTime)state;

            foreach (var rotater in rotaters)
            {
                try { rotater.Rotate(now); }
                catch (Exception e) { Trace.TraceError("rotate error : " + e); }
            }
        }

        public void Stop()
        {
            this.timer.Dispose();
        }

        public LogRotateOptions[] Reconfig()
        {
            var builders = new FileLogRotateOptionsBuilderProvider(optionsFile).CreateBuilders();
            var rotaters = new LogRotater[builders.Length];
            var options = new LogRotateOptions[builders.Length];
            for (int i = 0; i < builders.Length; i++)
            {
                options[i] = builders[i].Build();
                rotaters[i] = new DailyLogRotater(options[i]);
            }
            this.rotaters = rotaters;
            return options;
        }
    }

    public abstract class LogRotater
    {
        public LogRotateOptions Options { get; private set; }

        public LogRotater(LogRotateOptions options)
        {
            Options = options;
        }

        protected abstract bool CanTrigger(DateTime dateTime);

        public virtual void Rotate(DateTime dateTime)
        {
            if (!CanTrigger(dateTime)) return;
            Console.WriteLine("{0: mm:ss.fff}rotate", dateTime);

            var root = Options.Root;
            if (!Directory.Exists(root))
            {
                root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Options.Root);
                if (!Directory.Exists(root)) return;
            }

            RotateDirectory(root);

            NginxDaemon.Run("-s reopen");
        }

        void RotateDirectory(string root)
        {
            if (Options.IncludeSubDirs)
            {
                foreach (var subDir in Directory.GetDirectories(root))
                {
                    RotateDirectory(subDir);
                }
            }
            var files = Directory.GetFiles(root, Options.Filter);
            var regex = new Regex("^" + Options.Filter.Replace(".", "\\.").Replace("*", ".*") + "$");

            foreach (var file in files)
            {
                if (!regex.IsMatch(Path.GetFileName(file))) continue;

                RotateFile(file);
            }
        }

        protected void CompressFile(string file)
        {
            var gzFileName = file + ".gz";
            using (var gzFileStream = File.Create(gzFileName))
            {
                using (var gzStream = new GZipStream(gzFileStream, CompressionMode.Compress))
                {
                    using (var source = File.OpenRead(file))
                    {
                        var buffer = new byte[8192];
                        int size;
                        while ((size = source.Read(buffer, 0, 8192)) > 0)
                        {
                            gzStream.Write(buffer, 0, size);
                        }
                    }
                }
            }

            File.SetCreationTimeUtc(gzFileName, File.GetCreationTimeUtc(file));
            File.SetLastWriteTimeUtc(gzFileName, File.GetLastWriteTimeUtc(file));
        }

        protected void CleanFile(string file)
        {
            var files = Directory.GetFiles(Path.GetDirectoryName(file), Path.GetFileName(file) + "*");
            var expires = DateTime.Today.AddDays(-Options.Rotate);
            foreach (var rotateFile in files)
            {
                if (File.GetLastWriteTimeUtc(rotateFile) < expires) File.Delete(rotateFile);
            }
        }

        protected abstract void RotateFile(string file);
    }

    public class DailyLogRotater : LogRotater
    {
        private string time;
        public DailyLogRotater(LogRotateOptions options) : base(options)
        {
            if (options.RotateType != RotateType.Daily) throw new NotSupportedException();
            this.time = Options.RotateArguments ?? "0:00:00";
        }

        protected override bool CanTrigger(DateTime dateTime)
        {
            return dateTime.ToString("H:mm:ss") == time;
        }

        protected override void RotateFile(string file)
        {
            if (new FileInfo(file).Length == 0)
              File.Move(file, file + "-" + DateTime.Now.ToString("yyyyMMdd"));
              
            if (Options.Compress)
            {
                var date = DateTime.Today.AddDays(-Options.DelayCompress);
                var start = DateTime.Today.AddDays(-Options.Rotate);
                while (date >= start)
                {
                    var compressFile = file + "-" + date.ToString("yyyyMMdd");
                    if (!File.Exists(compressFile)) continue;

                    CompressFile(compressFile);
                    File.Delete(compressFile);

                    date = date.AddDays(-1);
                }
            }
            CleanFile(file);
        }
    }


    readonly NginxDaemon daemon;

    public NginxD(string serviceName)
    {
        ServiceName = serviceName;
        daemon = new NginxDaemon(this);
    }

    protected override void OnStart(string[] args)
    {
        daemon.Start();
    }

    protected override void OnStop()
    {
        daemon.Stop();
    }

    static void Main(string[] args)
    {
        if (Environment.UserInteractive)
        {
            if (args.Length > 0)
            {
                string control = args[0];
                string serviceName = args.Length > 1 ? args[1] : "nginx";
                switch (control)
                {
                    case "install":
                        ServiceManager.Install(serviceName
                                  , args.Length > 2 ? args[2] : null
                                  , args.Length > 3 ? args[3] : null);
                        break;
                    case "uninstall":
                        ServiceManager.Uninstall(serviceName);
                        break;
                    case "start":
                        ServiceManager.Start(serviceName);
                        break;
                    case "stop":
                        ServiceManager.Stop(serviceName);
                        break;
                }
            }
            NginxDaemonCommand.Send(string.Join(" ", args));
        }
        else
        {
            Run(new NginxD(args.Length == 0 ? "nginx" : args[0]));
        }
    }
}