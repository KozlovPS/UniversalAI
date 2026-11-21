namespace UniversalAI.Web.Components;

/// <summary>A viewing cursor over recorded calls. It never schedules or changes an experiment.</summary>
public sealed class ExperimentPlayback
{
    public enum Reveal { Stage, Question, Answer, Result }
    private IReadOnlyList<ExperimentViewData.Call> _calls = [];
    private int _index;
    private bool _active;
    public Reveal Phase { get; private set; } = Reveal.Stage;
    public string? StageId { get; private set; }
    public bool Playing { get; private set; } = true;
    public bool ShowingAll { get; private set; }
    public double Speed { get; set; } = 1;
    public ExperimentViewData.Call? Current => _index >= 0 && _index < _calls.Count ? _calls[_index] : null;
    public bool AtEnd => Current != null && _index == _calls.Count - 1 && Phase == Reveal.Result && !_active;
    public int ShownResults => ShowingAll ? _calls.Count : Current == null ? 0 : _index + (Phase == Reveal.Result ? 1 : 0);
    public int Total => _calls.Count;

    public void Update(IReadOnlyList<ExperimentViewData.Call> calls, bool active)
    {
        int? number = Current?.Number;
        _calls = calls;
        _active = active;
        if (number != null) _index = calls.ToList().FindIndex(c => c.Number == number);
        else _index = StageId == null ? 0 : calls.ToList().FindIndex(c => c.Step == StageId);
        if (Current != null) StageId = Current.Step;
    }

    public bool QuestionVisible(ExperimentViewData.Call call) => ShowingAll ||
        Current != null && (call.Number < Current.Number || call.Number == Current.Number && Phase >= Reveal.Question);
    public bool AnswerVisible(ExperimentViewData.Call call) => ShowingAll ||
        Current != null && (call.Number < Current.Number || call.Number == Current.Number && Phase >= Reveal.Answer);
    public bool ResultVisible(ExperimentViewData.Call call) => ShowingAll ||
        Current != null && (call.Number < Current.Number || call.Number == Current.Number && Phase == Reveal.Result);

    public bool Advance()
    {
        if (ShowingAll || Current == null) return false;
        if (Phase == Reveal.Question && Current.Response == null && Current.Error == null && _active) return false;
        if (Phase < Reveal.Result) { Phase++; return true; }
        if (_index + 1 >= _calls.Count) { if (!_active) Playing = false; return false; }
        string previousStage = Current.Step;
        _index++;
        StageId = Current!.Step;
        Phase = previousStage == StageId ? Reveal.Question : Reveal.Stage;
        return true;
    }

    public void Pause() => Playing = false;
    public void Play() { if (ShowingAll || AtEnd) Restart(); else Playing = true; }
    public void RevealAll() { ShowingAll = true; Playing = false; }
    public void Restart()
    {
        ShowingAll = false; Playing = true; _index = 0; Phase = Reveal.Stage;
        StageId = Current?.Step;
    }
    public void SeekStage(string id)
    {
        StageId = id; _index = _calls.ToList().FindIndex(c => c.Step == id);
        Phase = Reveal.Stage; Playing = false;
    }

    // Reading time affects presentation only. No model/token budgets are involved.
    public double DwellSeconds => Phase switch
    {
        Reveal.Stage => 1.4,
        Reveal.Question => 1.0,
        Reveal.Answer => Math.Clamp(1.8 + (Current?.Response?.Content.Length ?? 0) / 45.0, 2.4, 9),
        _ => 1.3
    };
}
