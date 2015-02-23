using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Xml.Serialization;
using System.Runtime.CompilerServices;
using System.Diagnostics;

namespace ServerClient
{
    static class Log
    {
        static private Action<string> log_ = msg => System.Console.WriteLine(msg);
        static private void LogWriteLineDirect(string msg, params object[] vals)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat(msg, vals);
            ConsoleLog(sb.ToString());
        }
        
        static public Action<string> ConsoleLog { set { log_ = value; } get { return log_; } }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static public string StDump(params object[] vals)
        {
            StackTrace st = new StackTrace();
            StackFrame sf = null;
            for (int i = 0; i < 10; ++i)
            {
                sf = st.GetFrame(i);
                if (sf == null)
                    break;

                string strClass = sf.GetMethod().DeclaringType.Name;

                if (strClass != typeof(Log).Name)
                    break;
            }

            string sMethod = sf.GetMethod().Name;
            string sClass = sf.GetMethod().DeclaringType.Name;

            string ret = sClass + "." + sMethod + " ";

            foreach (object o in vals)
                ret += o.ToString() + " ";

            return ret;
        }
        static public void Dump(params object[] vals)
        {
            Console(StDump(vals));
        }

        static public void Console(string s, params object[] vars)
        {
            string timestamp = String.Format("{0:hh:mm:ss.fff }", DateTime.Now);

            MasterFileLog.GetConsoleFileLog().LogWriteLine(timestamp + s, vars);
            Log.LogWriteLineDirect(s, vars);
        }
        static public void Entry(ILog l, LogLevel logLevel, LogParam pr, string s, params object[] vars)
        {
            if (l == null)
                return;

            bool useConsole = (pr & LogParam.CONSOLE) != 0 && (pr & LogParam.NO_CONSOLE) == 0;
            bool writeIntoLog = l.LogLevel >= (int)logLevel;

            if (!useConsole && !writeIntoLog)
                return;

            string timestamp = "";
            if (l.Timestamp)
                timestamp = String.Format("{0:hh:mm:ss.fff }", DateTime.Now);


            if (useConsole)
            {
                MasterFileLog.GetConsoleFileLog().LogWriteLine(timestamp + s, vars);
                Log.LogWriteLineDirect(s, vars);
            }

            if (writeIntoLog)
                l.LogWriteLine(timestamp + s, vars);
        }

        static public void EntryError(ILog l, string s, params object[] vars) { Entry(l, LogLevel.ERROR, LogParam.NO_OPTION, s, vars); }
        static public void EntryNormal(ILog l, string s, params object[] vars) { Entry(l, LogLevel.NORMAL, LogParam.NO_OPTION, s, vars); }
        static public void EntryVerbose(ILog l, string s, params object[] vars) { Entry(l, LogLevel.VERBOSE, LogParam.NO_OPTION, s, vars); }

        static public void EntryConsole(ILog l, string s, params object[] vars) { Entry(l, LogLevel.NORMAL, LogParam.CONSOLE, s, vars); }
    }

    [Flags]
    enum LogParam
    {
        NO_OPTION = 0,
        NO_CONSOLE = 1,
        CONSOLE = 2,
    }

    enum LogLevel : int
    {
        ERROR = 0,
        NORMAL = 1,
        VERBOSE = 2,
        ALL = 3
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
            String s = String.Format("{0:MM-dd HH.mm.ss}", DateTime.Now);

            string path = System.IO.Path.Combine(rootPath, s);

            if (Directory.Exists(path))
                path += Guid.NewGuid();

            return path;
        }

        public string Path{ get; private set; }

        public MasterFileLogger(string configName = "log_config.xml")
        {
            XmlSerializer ser = new XmlSerializer(typeof(LogConfig));

            try
            {
                using(StreamReader sr = new StreamReader(configName))
                    lc = (LogConfig)ser.Deserialize(sr);
            }
            catch (FileNotFoundException)
            {
                Log.ConsoleLog("Cannot open " + configName + ", creating default");
                
                using(StreamWriter sw = new StreamWriter(configName))
                    ser.Serialize(sw, lc);
            }

            Path = GetPath(lc.rootName);
            consoleLog = GetLog("Console.log");
        }

        public string CombinePaths(params string[] folders)
        {
            string ret = "";
            foreach(string fld in folders)
                ret = System.IO.Path.Combine(ret, fld);
            return ret;
        }

        public ILog GetLog(params string[] folders)
        {
            string path = CombinePaths(folders);
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
        static public ILog GetConsoleFileLog() { return mfl.GetConsoleLog(); }
        static public int LogLevel { get { return mfl.LogLevel; } }
    }

    public class LogConfig
    {
        public string rootName = "Logs";
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
