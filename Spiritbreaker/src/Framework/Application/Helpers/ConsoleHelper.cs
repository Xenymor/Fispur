using System;
using static Spiritbreaker.Application.Settings;

namespace Spiritbreaker.Application
{
    public static class ConsoleHelper
    {
        public static void Log(string msg, bool isError = false, ConsoleColor col = ConsoleColor.White)
        {
            bool log = MessagesToLog == LogType.All || (isError && MessagesToLog == LogType.ErrorOnly);

            if (log)
            {
                Console.ForegroundColor = col;
                Console.WriteLine(msg);
                Console.ResetColor();
            }
        }

    }
}
