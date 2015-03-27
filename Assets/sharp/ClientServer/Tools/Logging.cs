using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Xml.Serialization;
using System.Runtime.CompilerServices;
using System.Diagnostics;

namespace Tools
{
    [Flags]
    enum LogParam
    {
        NO_OPTION = 0,
        NO_CONSOLE = 1,
        CONSOLE = 2,
    }
    
    static class Log
    {       
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

        static string GetTimestamp() { return String.Format("{0:hh:mm:ss.fff }", DateTime.Now); }       

        static public void Console(string s, params object[] vars)
        {
            MasterLog.ConsoleWrite(s, GetTimestamp(), true, vars);
        }

        static public void Entry(ILog l, int logLevel, LogParam pr, string s, params object[] vars)
        {
            if (l == null)
                return;

            bool useConsole = (pr & LogParam.CONSOLE) != 0 && (pr & LogParam.NO_CONSOLE) == 0;
            bool writeIntoLog = l.LogLevel >= logLevel;

            if (!useConsole && !writeIntoLog)
                return;

            string timestamp = l.Timestamp ? GetTimestamp() : "";

            if (useConsole)
                MasterLog.ConsoleWrite(s, timestamp, true, vars);

            if (writeIntoLog)
                l.LogWriteLine(timestamp + s, vars);
        }

        static public void EntryError(ILog l, string s, params object[] vars) { Entry(l, 0, LogParam.NO_OPTION, s, vars); }
        static public void EntryNormal(ILog l, string s, params object[] vars) { Entry(l, 1, LogParam.NO_OPTION, s, vars); }
        static public void EntryVerbose(ILog l, string s, params object[] vars) { Entry(l, 2, LogParam.NO_OPTION, s, vars); }

        static public void EntryConsole(ILog l, string s, params object[] vars) { Entry(l, 1, LogParam.CONSOLE, s, vars); }
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
        private string path;

        public FileLog(string path, int logLevel_arg)
            :base(logLevel_arg)
        {
            this.path = path;
            
            Directory.CreateDirectory(Path.GetDirectoryName(path));
        }

        public override void LogWrite(string s)
        {
            using(var fs = new StreamWriter(path, true))
                fs.Write(s);
        }
    }
    class DummyLog : ILog
    {
        public DummyLog(int logLevel_arg) : base(logLevel_arg) { }
        public override void LogWrite(string s) { }
    }

    class UTIL_Logger
    {
        public string Path { get; private set; }
        public int LogLevel { get { return lc.logLevel; } }

        public ILog GetFileLog(params string[] folders)
        {
            lock (locker)
            {
                if (LogLevel < 0)
                    return new DummyLog(LogLevel);
                
                string path = CombinePaths(folders);
                path = System.IO.Path.Combine(Path, path);

                return new FileLog(path, LogLevel);
            }
        }

        public void ConsoleWrite(string msg, string timestamp, bool fileWrite, params object[] vars)
        {
            lock (locker)
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendFormat(msg, vars);

                string message = sb.ToString();

                consoleLog(message);

                if (fileWrite)
                    consoleFileLog.LogWrite(timestamp + message);
            }
        }

        public UTIL_Logger(string configName, Action<string> consoleLog)
        {
            this.consoleLog = consoleLog;
            
            XmlSerializer ser = new XmlSerializer(typeof(LogConfig));

            try
            {
                using (StreamReader sr = new StreamReader(configName))
                    lc = (LogConfig)ser.Deserialize(sr);
            }
            catch (FileNotFoundException)
            {
                consoleLog("Cannot open " + configName + ", creating default");

                using (StreamWriter sw = new StreamWriter(configName))
                    ser.Serialize(sw, lc);
            }

            Path = GetPath(lc.rootName);
            consoleFileLog = GetFileLog("Console.log");
        }

        private string GetPath(string rootPath)
        {
            String s = String.Format("{0:MM-dd HH.mm.ss}", DateTime.Now);

            string path = System.IO.Path.Combine(rootPath, s);

            if (Directory.Exists(path))
                path += Guid.NewGuid();

            return path;
        }
        private string CombinePaths(params string[] folders)
        {
            string ret = "";
            foreach(string fld in folders)
                ret = System.IO.Path.Combine(ret, fld);
            return ret;
        }

        private object locker = new object();
        private readonly ILog consoleFileLog;
        private readonly Action<string> consoleLog;// = msg => System.Console.WriteLine(msg);

        private readonly LogConfig lc = new LogConfig();
    }

    static class MasterLog
    {
        static private UTIL_Logger mfl = null;

        static public void Initialize(string configName, Action<string> consoleLog) { mfl = new UTIL_Logger(configName, consoleLog); }
        
        static public ILog GetFileLog(params string[] folders) { return mfl.GetFileLog(folders); }
        static public int LogLevel { get { return mfl.LogLevel; } }

        static public void ConsoleWrite(string msg, string timestamp, bool fileWrite, params object[] vars) 
            { mfl.ConsoleWrite(msg, timestamp, fileWrite, vars); }
    }

    public class LogConfig
    {
        public string rootName = "Logs";
        public int logLevel = -1;
        public List<LogConfigEntry> logConfigList = new List<LogConfigEntry>();
    }

    public class LogConfigEntry
    {
        public string name;
        public string logLevel;
        public string output;
    }
}
