using System;
using System.Threading;

namespace JSE
{
    public class Threader
    {
        public Threader()
        {
        }

        public static Thread StartNamedBackgroundThread(string ThreadName, Action d)
        {
            Thread thread = new Thread((object start) =>
            {
                try
                {
                    d();
                }
                catch (Exception exception)
                {
                    throw exception;
                }
            })
            {
                Name = ThreadName,
                IsBackground = true
            };
            thread.Start();
            return thread;
        }

        public static Thread StartNamedThread(string ThreadName, Action d)
        {
            Thread thread = new Thread((object start) =>
            {
                try
                {
                    d();
                }
                catch (Exception exception)
                {
                    throw exception;
                }
            })
            {
                Name = ThreadName
            };
            thread.Start();
            return thread;
        }
    }
}