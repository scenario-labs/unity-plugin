using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;

namespace Scenario.Editor
{
    /// <summary>
    /// Runs work on the editor's main thread from any thread (the agent server
    /// answers on worker threads; Unity's API only works on the main thread).
    /// An async function started here resumes on the main thread after each await.
    /// </summary>
    [InitializeOnLoad]
    public static class MainThread
    {
        static readonly ConcurrentQueue<Action> Queue = new ConcurrentQueue<Action>();
        static int _mainThreadId;

        static MainThread()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            EditorApplication.update += Pump;
        }

        public static bool IsMainThread => Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        static void Pump()
        {
            // A bounded batch per frame keeps the editor responsive.
            for (var i = 0; i < 64 && Queue.TryDequeue(out var action); i++)
            {
                try { action(); }
                catch (Exception e) { UnityEngine.Debug.LogException(e); }
            }
        }

        public static void Post(Action action) => Queue.Enqueue(action);

        public static Task<T> Run<T>(Func<T> work)
        {
            if (IsMainThread) return Task.FromResult(work());
            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Queue.Enqueue(() =>
            {
                try { done.SetResult(work()); }
                catch (Exception e) { done.SetException(e); }
            });
            return done.Task;
        }

        /// <summary>Starts an async function on the main thread and completes when it does.</summary>
        public static Task<T> RunAsync<T>(Func<Task<T>> work)
        {
            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Start()
            {
                Task<T> task;
                try { task = work(); }
                catch (Exception e)
                {
                    done.SetException(e);
                    return;
                }
                task.ContinueWith(t =>
                {
                    if (t.IsFaulted) done.SetException(t.Exception.InnerExceptions);
                    else if (t.IsCanceled) done.SetCanceled();
                    else done.SetResult(t.Result);
                }, TaskContinuationOptions.ExecuteSynchronously);
            }
            if (IsMainThread) Start();
            else Queue.Enqueue(Start);
            return done.Task;
        }
    }
}
