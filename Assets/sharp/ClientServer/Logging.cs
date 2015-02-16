using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Xml.Serialization;

namespace ServerClient
{
    [Flags]
    enum LogParam
    {
        NO_OPTION = 0,
        NO_CONSOLE = 1,
        CONSOLE = 2,
    }
    
    abstract class ILog
    {
        public int LogLevel { get; private set; }
        public bool Timestamp { get; private set; }

        public ILog(int logLevel_arg)
        {
            LogLevel = logLevel_arg;
            Timestamp = true;
        }


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

        static public void Entry(ILog l, int logLevel, LogParam pr, string s, params object[] vars)
        {
            if (l == null)
                return;

            bool useConsole = (pr & LogParam.CONSOLE) != 0 && (pr & LogParam.NO_CONSOLE) == 0;
            bool writeIntoLog = l.LogLevel >= logLevel;

            if (!useConsole && !writeIntoLog)
                return;

            string timestamp = "";
            if(l.Timestamp)
                timestamp = String.Format("{0:hh:mm:ss.fff }", DateTime.Now);


            if (useConsole)
            {
                MasterFileLog.GetConsoleLog().LogWriteLine(timestamp + s, vars);
                Log.LogWriteLine(s, vars);
            }

            if (writeIntoLog)
                l.LogWriteLine(timestamp + s, vars);
        }

        static public void EntryError(ILog l, string s, params object[] vars)   { Entry(l, 0, LogParam.NO_OPTION, s, vars); }
        static public void EntryNormal(ILog l, string s, params object[] vars)  { Entry(l, 1, LogParam.NO_OPTION, s, vars); }
        static public void EntryVerbose(ILog l, string s, params object[] vars) { Entry(l, 2, LogParam.NO_OPTION, s, vars); }

        static public void EntryConsole(ILog l, string s, params object[] vars) { Entry(l, 1, LogParam.CONSOLE, s, vars); }
    }

    class FileLog : ILog
    {
        StreamWriter fs;

        public FileLog(string path, int logLevel_arg)
            :base(logLevel_arg)
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

    //class MultiLog : ILog
    //{
    //    private List<ILog> logs;

    //    public MultiLog(params ILog[] logs_arg) { logs = new List<ILog>(logs_arg); }
    //    public void AddLog(ILog l) { logs.Add(l); }

    //    public override void LogWrite(string s)
    //    {
    //        foreach(var l in logs)
    //            l.LogWrite(s);
    //    }

    //}

    class MasterFileLogger
    {
        static string GetPath(string rootPath)
        {
            String s = String.Format("{0:MM-yy HH.mm.ss}", DateTime.Now);

            string path = System.IO.Path.Combine(rootPath, s);

            if (Directory.Exists(path))
                path += Guid.NewGuid();

            return path;
        }

        public string Path{ get; private set; }

        public MasterFileLogger(string rootPath = "Logs")
        {
            XmlSerializer ser = new XmlSerializer(typeof(LogConfig));

            StreamReader sr = new StreamReader("log_config.xml");
            lc = (LogConfig)ser.Deserialize(sr);

            Path = GetPath(rootPath);

            consoleLog = GetLog("Console.log");
        }

        public ILog GetLog(params string[] folders)
        {
            string path = System.IO.Path.Combine(folders);
            path = System.IO.Path.Combine(Path, path);

            return new FileLog(path, lc.logLevel);
        }

        private ILog consoleLog;
        public ILog GetConsoleLog()
        {
            return consoleLog;
        }

        LogConfig lc = new LogConfig();

        public int LogLevel { get { return lc.logLevel; } }
    }

    static class MasterFileLog
    {
        static private MasterFileLogger mfl = new MasterFileLogger();
        static public ILog GetLog(params string[] folders)
        {
            return mfl.GetLog(folders);
        }
        static public ILog GetConsoleLog() { return mfl.GetConsoleLog(); }
        static public int LogLevel { get { return mfl.LogLevel; } }
    }

    public class LogConfig
    {
        public int logLevel = 1;
        public List<LogConfigEntry> logConfigList = new List<LogConfigEntry>();
    }

    public class LogConfigEntry
    {
        public string name;
        public string logLevel;
        public string output;
    }
}
