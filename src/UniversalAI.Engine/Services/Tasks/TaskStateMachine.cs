using System.Text.Json;
using System.Text.Json.Serialization;

namespace UniversalAI.Services.Tasks;

[JsonConverter(typeof(JsonStringEnumConverter<TaskStage>))]
public enum TaskStage { Planning, Execution, Validation, Done }

public sealed record TaskEvent(string Action, TaskStage? Before, TaskStage After, bool Paused, DateTimeOffset At);

public sealed record TaskWorkflowState
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Goal { get; init; } = "";
    public TaskStage Stage { get; init; } = TaskStage.Planning;
    public bool Paused { get; init; }
    public int Revision { get; init; } = 1;
    public string Plan { get; init; } = "";
    public bool PlanApproved { get; init; }
    public int? ApprovedPlanRevision { get; init; }
    public string Implementation { get; init; } = "";
    public string Validation { get; init; } = "";
    public string Result { get; init; } = "";
    public List<TaskEvent> Events { get; init; } = [];
    public string CurrentStep => Stage switch
    {
        TaskStage.Planning => Plan.Length == 0 ? "Подготовить план" : "Получить утверждение плана пользователя",
        TaskStage.Execution => Implementation.Length == 0 ? "Выполнить утверждённый план" : "Проверить полученный результат",
        TaskStage.Validation => Validation.Length == 0 ? "Выполнить проверку" : "Подготовить финальный ответ после проверки",
        _ => "Задача завершена"
    };
    public string ExpectedAction => Paused ? "resume" : Stage switch
    {
        TaskStage.Planning => Plan.Length == 0 ? "plan" : "approve",
        TaskStage.Execution => Implementation.Length == 0 ? "execute" : "validate",
        TaskStage.Validation => Validation.Length == 0 ? "validate" : "finish",
        _ => "start / clear / status"
    };
}

public sealed record TaskDecision(bool Allowed, string Action, string Reason, TaskWorkflowState? State,
    string? Generation = null);

/// <summary>Only this code creates transitions; model output never deserialises into state.</summary>
public static class TaskStateMachine
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static TaskWorkflowState? Parse(string json)
    {
        var state = JsonSerializer.Deserialize<TaskWorkflowState>(json, Json);
        if (state != null) EnsureConsistent(state);
        return state;
    }
    public static void EnsureConsistent(TaskWorkflowState s)
    {
        if (!Enum.IsDefined(s.Stage) || string.IsNullOrWhiteSpace(s.Goal) || string.IsNullOrWhiteSpace(s.Id) || s.Revision < 1 ||
            s.Plan == null || s.Implementation == null || s.Validation == null || s.Result == null || s.Events == null ||
            s.Stage >= TaskStage.Execution && (!s.PlanApproved || string.IsNullOrWhiteSpace(s.Plan) || s.ApprovedPlanRevision == null) ||
            s.Stage >= TaskStage.Validation && string.IsNullOrWhiteSpace(s.Implementation) ||
            s.Stage == TaskStage.Done && (string.IsNullOrWhiteSpace(s.Validation) || string.IsNullOrWhiteSpace(s.Result)))
            throw new InvalidOperationException("Сохранённое состояние задачи нарушает правила автомата.");
    }

    public static TaskDecision Decide(TaskWorkflowState? state, string action, bool explicitCommand,
        string? goal = null, int? expectedRevision = null)
    {
        if (state != null) EnsureConsistent(state);
        TaskDecision Deny(string reason) => new(false, action, reason, state);
        if (expectedRevision.HasValue && expectedRevision != (state?.Revision ?? 0))
            return Deny("Состояние уже изменилось. Обновите диалог и повторите действие.");
        if (action == "status") return new(true, action, Describe(state), state);
        if (action == "start")
        {
            if (!explicitCommand) return Deny("Создать новую задачу можно только явным действием пользователя.");
            if (state != null && (state.Stage != TaskStage.Done || state.Paused)) return Deny("Сначала завершите текущую задачу; новая цель не заменяет активное состояние.");
            if (string.IsNullOrWhiteSpace(goal)) return Deny("Укажите цель задачи после /task start.");
            var created = new TaskWorkflowState { Goal = goal.Trim(), Revision = (state?.Revision ?? 0) + 1 };
            return new(true, action, "Задача создана. Следующий шаг — подготовка плана.",
                created with { Events = [new(action, state?.Stage, created.Stage, false, DateTimeOffset.UtcNow)] });
        }
        if (state == null) return Deny("Нет текущей задачи. Создайте её: /task start <цель>.");
        if (action == "continue")
        {
            if (state.Paused) action = "resume";
            else if (state.Stage == TaskStage.Planning && state.Plan.Length > 0)
                return Deny("План ожидает утверждения. Используйте кнопку «Утвердить план» или /task approve.");
            else action = state.Stage switch
            {
                TaskStage.Planning => "plan", TaskStage.Execution => state.Implementation.Length == 0 ? "execute" : "validate",
                TaskStage.Validation => state.Validation.Length == 0 ? "validate" : "finish", _ => "finish"
            };
        }
        if (action == "resume")
            return state.Paused ? Change(state with { Paused = false }, action, "Задача продолжена с сохранённого шага.") : Deny("Задача не находится на паузе.");
        if (state.Paused) return Deny("Задача на паузе. Сначала продолжите её: /task resume. Этап и результаты сохранены.");
        if (action == "pause") return Change(state with { Paused = true }, action, "Задача приостановлена. Этап, шаг и результаты сохранены.");
        if (action == "clear")
            return explicitCommand && state.Stage == TaskStage.Done
                ? new(true, action, "Завершённая задача убрана из диалога.", null)
                : Deny("Очистка доступна явной командой только после завершения задачи.");
        if (action == "approve")
        {
            if (!explicitCommand) return Deny("Модель не может утвердить план. Нужны кнопка пользователя или точная команда /task approve.");
            if (state.Stage != TaskStage.Planning || string.IsNullOrWhiteSpace(state.Plan)) return Deny("Утверждение возможно только после подготовки плана на этапе planning.");
            return Change(state with { Stage = TaskStage.Execution, PlanApproved = true, ApprovedPlanRevision = state.Revision }, action, "Пользователь утвердил сохранённую версию плана. Разрешена реализация.");
        }
        return action switch
        {
            "plan" when state.Stage == TaskStage.Planning => new(true, action, "Разрешена подготовка плана.", state, "plan"),
            "execute" when state.Stage == TaskStage.Execution && state.PlanApproved => new(true, action, "Реализация разрешена утверждённым планом.", state, "execute"),
            "validate" when state.Stage is TaskStage.Execution or TaskStage.Validation && state.Implementation.Length > 0 => new(true, action, "Разрешена проверка сохранённого результата.", state, "validate"),
            "finish" when state.Stage == TaskStage.Validation && state.Validation.Length > 0 => new(true, action, "Валидация выполнена; разрешён финальный ответ.", state, "finish"),
            "execute" => Deny("Реализация запрещена: нужен подготовленный и явно утверждённый план на этапе execution."),
            "validate" => Deny("Переход к validation запрещён: сначала выполните реализацию утверждённого плана."),
            "finish" => Deny("Переход к done запрещён: сначала planning, утверждение, execution и выполненная validation. Этапы нельзя пропускать."),
            "plan" => Deny("План нельзя заменить после его утверждения в этой задаче."),
            _ => Deny("Неизвестное действие. Используйте /task status, plan, approve, execute, validate, finish, pause или resume.")
        };

        TaskDecision Change(TaskWorkflowState updated, string performed, string reason) => new(true, performed, reason,
            updated with { Revision = state.Revision + 1, Events = [.. state.Events, new(performed, state.Stage, updated.Stage, updated.Paused, DateTimeOffset.UtcNow)] });
    }

    public static TaskWorkflowState Complete(TaskDecision decision, string content)
    {
        if (!decision.Allowed || decision.Generation == null || decision.State == null || string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException("Нельзя применить результат запрещённого или незавершённого этапа.");
        var s = decision.State;
        var rechecked = Decide(s, decision.Generation, explicitCommand: false);
        if (!rechecked.Allowed || rechecked.Generation != decision.Generation)
            throw new InvalidOperationException("Переход повторно отклонён перед применением результата этапа.");
        var updated = decision.Generation switch
        {
            "plan" => s with { Plan = content }, "execute" => s with { Implementation = content },
            "validate" => s with { Stage = TaskStage.Validation, Validation = content },
            "finish" => s with { Stage = TaskStage.Done, Result = content },
            _ => throw new InvalidOperationException("Неизвестный этап генерации.")
        };
        updated = updated with { Revision = s.Revision + 1, Events = [.. s.Events, new(decision.Generation, s.Stage, updated.Stage, updated.Paused, DateTimeOffset.UtcNow)] };
        EnsureConsistent(updated);
        return updated;
    }
    public static string Describe(TaskWorkflowState? s) => s == null ? "Текущая задача отсутствует." :
        $"Этап: {s.Stage.ToString().ToLowerInvariant()}; {(s.Paused ? "пауза" : "активна")}; шаг: {s.CurrentStep}; ожидается: {s.ExpectedAction}; версия: {s.Revision}.";
}
