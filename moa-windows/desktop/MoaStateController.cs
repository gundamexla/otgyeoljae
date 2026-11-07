using System;

namespace Moa
{
    public enum Motion { Idle, Listen, Work, Done, Confirm }
    public enum Outcome { None, Running, Completed, Cancelled, Error, Disconnected }

    // Owned by the UI thread. Every stream callback must carry its original request ID.
    public sealed class MoaStateController
    {
        public Motion Motion { get; private set; }
        public Outcome Outcome { get; private set; }
        public string RequestId { get; private set; }
        public string Draft { get; private set; }
        public string Answer { get; private set; }
        public string Notice { get; private set; }
        public bool NeedsChoice { get; private set; }
        public bool Busy { get { return RequestId != null; } }
        public event Action Changed;
        public bool LocalMode { get; set; }

        public MoaStateController() { Draft = Answer = ""; Notice = "이야기를 들을 준비가 됐어요."; }
        private void Publish() { if (Changed != null) Changed(); }
        public void Editing(bool hasInput)
        {
            if (Busy) return;
            // Preserve terminal notices until a new request is submitted.
            Motion = hasInput ? Motion.Listen : Motion.Idle;
            Publish();
        }
        public string Begin(bool needsChoice)
        {
            if (Busy) throw new InvalidOperationException("이미 진행 중인 요청이 있어요.");
            RequestId = Guid.NewGuid().ToString("N");
            Draft = Answer = "";
            Outcome = Outcome.Running;
            NeedsChoice = needsChoice;
            Motion = needsChoice ? Motion.Confirm : Motion.Work;
            Notice = needsChoice ? "어떤 응답을 볼까요? 아래에서 골라 주세요." : "모아가 답변을 준비하고 있어요.";
            Publish();
            return RequestId;
        }
        public bool Choose(string id)
        {
            if (id != RequestId || !Busy || !NeedsChoice) return false;
            NeedsChoice = false;
            Motion = Motion.Work;
            Notice = "선택한 응답을 준비하고 있어요.";
            Publish();
            return true;
        }
        public void Receive(string id, string kind, string text)
        {
            if (!Busy || RequestId != id || NeedsChoice) return;
            switch (kind)
            {
                case "queued": Motion = Motion.Work; break;
                case "delta": Draft += text; Motion = Motion.Work; break;
                case "done":
                    Answer = text; Draft = ""; RequestId = null;
                    Outcome = Outcome.Completed; Motion = Motion.Done;
                    Notice = LocalMode ? "로컬 AI의 답변이 도착했어요." : "모의 답변이 도착했어요."; break;
                case "error": Fail(Outcome.Error, "오류 · " + text); return;
                default: return;
            }
            Publish();
        }
        public void StreamEnded(string id)
        {
            if (Busy && RequestId == id) Fail(Outcome.Disconnected, "연결 끊김 · 답변 완료를 확인하지 못했어요. 다시 보내 주세요.");
        }
        public void Cancel()
        {
            if (Busy) Fail(Outcome.Cancelled, "취소됨 · 답변을 중지했어요. 다시 입력할 수 있어요.");
        }
        private void Fail(Outcome outcome, string notice)
        {
            RequestId = null; Draft = Answer = ""; NeedsChoice = false;
            Outcome = outcome; Motion = Motion.Idle; Notice = notice; Publish();
        }
    }
}
