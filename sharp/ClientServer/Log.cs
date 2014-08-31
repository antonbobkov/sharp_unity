using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ServerClient
{
    class Log
    {
        static public Action<string> log = msg => Console.WriteLine(msg);

        static public void LogWriteLine(string msg, params object[] vals)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat(msg, vals);
            log(sb.ToString());
        }
    }
}
