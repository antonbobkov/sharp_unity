using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;

namespace ServerClient
{
    abstract class ILog
    {
        public abstract void LogWrite(string s);
        public void LogWrite(string s, params object[] vars)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat(s, vars);
            LogWrite(sb.ToString());
        }
        public void LogWriteLine(string s, params object[] vars)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat(s, vars);
            sb.AppendLine();
            LogWrite(sb.ToString());
        }
    }

    class ConsoleLog : ILog
    {
        public override void LogWrite(string s)
        {
            Console.Write(s);
        }
    }

    class FileLog : ILog
    {
        StreamWriter fs;

        public FileLog(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            fs = new StreamWriter(path, false);
        }

        public override void LogWrite(string s)
        {
            fs.Write(s);
            fs.Flush();
        }
    }

    class PiggyBackLog : ILog
    {
        ILog piggy;
        public PiggyBackLog(ILog piggy) { this.piggy = piggy; }
        public override void LogWrite(string s)
        {
            piggy.LogWrite(s);
        }

    }

    class MasterFileLogger
    {
        static string GetPath(string rootPath)
        {
            DateTime dt = DateTime.Now;
            String s = String.Join("_", dt.Month.ToString("00"), dt.Day.ToString("00")) + " " +
                        dt.Hour.ToString("00") + "h" + dt.Minute.ToString("00") + "m" + dt.Second.ToString("00") + "s";

            string path = System.IO.Path.Combine(rootPath, s);

            if (Directory.Exists(path))
                path += Guid.NewGuid();

            return path;
        }

        public string Path{ get; private set; }

        public MasterFileLogger(string rootPath = "Logs")
        {
            Path = GetPath(rootPath);
        }

        public ILog GetLog(params string[] folders)
        {
            string path = System.IO.Path.Combine(folders);
            path = System.IO.Path.Combine(Path, path);

            return new FileLog(path);
        }
    }

    static class MasterFileLog
    {
        static MasterFileLogger mfl = new MasterFileLogger();
        static public ILog GetLog(params string[] folders)
        {
            return mfl.GetLog(folders);
        }
    }

    public class LogConfig
    {
        public string defaultLogLevel;
        public List<LogConfigEntry> logConfigList = new List<LogConfigEntry>();
    }

    public class LogConfigEntry
    {
        public string name;
        public string logLevel;
        public string output;
    }
}
