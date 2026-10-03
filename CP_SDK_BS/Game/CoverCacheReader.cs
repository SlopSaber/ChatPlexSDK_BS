using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CP_SDK_BS.Game
{
    internal static class CoverCacheReader
    {
        internal sealed class Completion
        {
            private byte[]      m_Bytes;
            private bool        m_Valid;
            private Exception   m_Error;
            private int         m_State;

            internal void CompleteNetwork(bool p_Valid, byte[] p_Bytes)
                => Complete(p_Bytes, p_Valid, null);

            internal void Complete(byte[] p_Bytes, bool p_Valid, Exception p_Error)
            {
                if (Interlocked.CompareExchange(ref m_State, 1, 0) != 0)
                    return;

                m_Bytes = p_Bytes;
                m_Valid = p_Valid;
                m_Error = p_Error;
                Volatile.Write(ref m_State, 2);
            }

            internal bool TryGetResult(out byte[] p_Bytes, out bool p_Valid, out Exception p_Error)
            {
                if (Volatile.Read(ref m_State) != 2)
                {
                    p_Bytes = null;
                    p_Valid = false;
                    p_Error = null;
                    return false;
                }

                p_Bytes = m_Bytes;
                p_Valid = m_Valid;
                p_Error = m_Error;
                return true;
            }
        }

        internal sealed class Request
        {
            internal readonly string        Path;
            internal readonly Completion    Result = new Completion();

            internal Request(string p_Path)
            {
                Path = p_Path;
            }
        }

        private static readonly Queue<Request> m_Requests = new Queue<Request>();
        private static Task m_Worker;

        internal static Request Read(string p_Path)
        {
            var l_Request = new Request(p_Path);
            lock (m_Requests)
            {
                m_Requests.Enqueue(l_Request);
                if (m_Worker == null || m_Worker.IsCompleted)
                    StartWorker();
            }
            return l_Request;
        }

        internal static Request Failed(Exception p_Error)
        {
            var l_Request = new Request(null);
            l_Request.Result.Complete(null, false, p_Error);
            return l_Request;
        }

        private static void StartWorker()
        {
            try
            {
                if (ExecutionContext.IsFlowSuppressed())
                    CreateWorker();
                else
                {
                    using (ExecutionContext.SuppressFlow())
                        CreateWorker();
                }
            }
            catch (Exception l_Exception)
            {
                while (m_Requests.Count != 0)
                    m_Requests.Dequeue().Result.Complete(null, false, l_Exception);
            }
        }

        private static void CreateWorker()
        {
            var l_Worker = new Task(Drain, CancellationToken.None, TaskCreationOptions.DenyChildAttach);
            l_Worker.ContinueWith(WorkerCompleted, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            l_Worker.Start(TaskScheduler.Default);
            m_Worker = l_Worker;
        }

        private static void WorkerCompleted(Task p_Worker)
        {
            lock (m_Requests)
            {
                if (ReferenceEquals(m_Worker, p_Worker) && p_Worker.IsCompleted && m_Requests.Count != 0)
                    StartWorker();
            }
        }

        private static void Drain()
        {
            while (true)
            {
                Request l_Request;
                lock (m_Requests)
                {
                    if (m_Requests.Count == 0)
                        return;
                    l_Request = m_Requests.Dequeue();
                }

                try
                {
                    var l_Bytes = File.Exists(l_Request.Path) ? File.ReadAllBytes(l_Request.Path) : null;
                    l_Request.Result.Complete(l_Bytes, l_Bytes != null, null);
                }
                catch (Exception l_Exception)
                {
                    l_Request.Result.Complete(null, false, l_Exception);
                }
            }
        }
    }
}
