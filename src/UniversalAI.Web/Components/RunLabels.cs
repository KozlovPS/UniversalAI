using UniversalAI.Benchmarks.Runtime;
namespace UniversalAI.Web.Components;
public static class RunLabels
{
    public static string Status(RunStatus status) => status switch
    {
        RunStatus.Queued => "В очереди", RunStatus.Running => "Выполняется", RunStatus.Completed => "Завершён",
        RunStatus.Failed => "Ошибка", RunStatus.Cancelled => "Отменён", RunStatus.Interrupted => "Прерван", _ => status.ToString()
    };
}
