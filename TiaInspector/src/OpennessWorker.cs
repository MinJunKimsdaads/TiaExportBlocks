using System;
using System.Collections.Concurrent;
using System.Threading;

namespace TiaInspector
{
    /// <summary>
    /// Openness 호출 전용 스레드 하나.
    ///
    /// TiaPortal 객체는 스레드 안전하지 않다. UI 스레드에서 붙고 워커
    /// 스레드에서 읽으면 COM 마샬링에 의존하게 되는데, 대형 프로젝트에서는
    /// 그 비용이 그대로 체감된다. 그래서 연결·조회·추출을 전부 이 스레드
    /// 하나에 몰아 직렬화한다 — UI 는 그동안 자유롭고, Openness 는 언제나
    /// 같은 스레드에서만 불린다.
    /// </summary>
    internal sealed class OpennessWorker : IDisposable
    {
        readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();
        readonly Thread _thread;

        public OpennessWorker()
        {
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "TiaInspector.Openness"
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        void Loop()
        {
            foreach (Action work in _queue.GetConsumingEnumerable())
            {
                try { work(); }
                catch { /* 각 작업이 자기 예외를 UI 로 보고한다 */ }
            }
        }

        public void Post(Action work)
        {
            if (_queue.IsAddingCompleted) return;
            _queue.Add(work);
        }

        public void Dispose()
        {
            try { _queue.CompleteAdding(); } catch { }
            try { _thread.Join(TimeSpan.FromSeconds(5)); } catch { }
        }
    }
}
