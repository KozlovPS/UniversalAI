using System.Globalization;
using System.Text.Json;
using UniversalAI.Core;

namespace UniversalAI.Services.Agents;

public sealed class AgentToolRegistry
{
    private readonly Dictionary<string, AgentTool> _tools;

    public AgentToolRegistry()
    {
        _tools = new Dictionary<string, AgentTool>(StringComparer.OrdinalIgnoreCase)
        {
            ["get_current_time"] = new(
                new ChatFunctionDefinition
                {
                    Name = "get_current_time",
                    Description = "Возвращает текущие дату, время и часовой пояс этого компьютера.",
                    Parameters = Schema(new { type = "object", properties = new { }, required = Array.Empty<string>() })
                },
                _ => JsonSerializer.Serialize(new
                {
                    current_time = DateTimeOffset.Now.ToString(
                        "yyyy-MM-dd HH:mm:ss zzz",
                        CultureInfo.InvariantCulture)
                })),

            ["calculate"] = new(
                new ChatFunctionDefinition
                {
                    Name = "calculate",
                    Description = "Вычисляет арифметическое выражение с числами, скобками и операциями +, -, *, /.",
                    Parameters = Schema(new
                    {
                        type = "object",
                        properties = new
                        {
                            expression = new
                            {
                                type = "string",
                                description = "Например: (14 * 60) + 32. Десятичный разделитель — точка."
                            }
                        },
                        required = new[] { "expression" }
                    })
                },
                arguments =>
                {
                    string expression = GetRequiredString(arguments, "expression");
                    double result = SafeArithmeticParser.Evaluate(expression);
                    return JsonSerializer.Serialize(new
                    {
                        expression,
                        result
                    });
                }),

            ["roll_dice"] = new(
                new ChatFunctionDefinition
                {
                    Name = "roll_dice",
                    Description = "Бросает один или несколько одинаковых кубиков.",
                    Parameters = Schema(new
                    {
                        type = "object",
                        properties = new
                        {
                            sides = new { type = "integer", description = "Число граней одного кубика, от 2 до 1000." },
                            count = new { type = "integer", description = "Количество кубиков, от 1 до 10." }
                        },
                        required = new[] { "sides", "count" }
                    })
                },
                arguments =>
                {
                    int sides = GetRequiredInt32(arguments, "sides");
                    int count = GetRequiredInt32(arguments, "count");
                    if (sides is < 2 or > 1000)
                    {
                        throw new ArgumentOutOfRangeException(
                            "sides", "Число граней должно быть от 2 до 1000.");
                    }

                    if (count is < 1 or > 10)
                    {
                        throw new ArgumentOutOfRangeException(
                            "count", "Количество кубиков должно быть от 1 до 10.");
                    }

                    int[] results = Enumerable.Range(0, count)
                        .Select(_ => Random.Shared.Next(1, sides + 1))
                        .ToArray();
                    return JsonSerializer.Serialize(new
                    {
                        sides,
                        results,
                        total = results.Sum()
                    });
                })
        };
    }

    public IReadOnlyList<ChatFunctionDefinition> Definitions =>
        _tools.Values.Select(tool => tool.Definition).ToList();

    public string Execute(string name, string arguments)
    {
        if (!_tools.TryGetValue(name, out AgentTool? tool))
        {
            return JsonSerializer.Serialize(new { error = $"Неизвестный инструмент: {name}" });
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(arguments);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Аргументы должны быть JSON-объектом.");
            }

            return tool.Execute(document.RootElement);
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException or
            ArgumentException or FormatException or ArithmeticException)
        {
            return JsonSerializer.Serialize(new { error = exception.Message });
        }
    }

    private static JsonElement Schema<T>(T value) =>
        JsonSerializer.SerializeToElement(value);

    private static string GetRequiredString(JsonElement arguments, string name)
    {
        if (!arguments.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new ArgumentException($"Не задан строковый параметр '{name}'.");
        }

        return value.GetString()!;
    }

    private static int GetRequiredInt32(JsonElement arguments, string name)
    {
        if (!arguments.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out int result))
        {
            throw new ArgumentException($"Не задан целочисленный параметр '{name}'.");
        }

        return result;
    }

    private sealed record AgentTool(
        ChatFunctionDefinition Definition,
        Func<JsonElement, string> Execute);
}

internal sealed class SafeArithmeticParser
{
    private readonly string _expression;
    private int _position;

    private SafeArithmeticParser(string expression)
    {
        _expression = expression;
    }

    public static double Evaluate(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression) || expression.Length > 200)
        {
            throw new ArgumentException("Выражение пустое или слишком длинное.");
        }

        SafeArithmeticParser parser = new(expression);
        double result = parser.ParseExpression();
        parser.SkipWhitespace();
        if (parser._position != parser._expression.Length)
        {
            throw new FormatException("В выражении есть неподдерживаемые символы.");
        }

        if (!double.IsFinite(result))
        {
            throw new ArithmeticException("Результат не является конечным числом.");
        }

        return result;
    }

    private double ParseExpression()
    {
        double value = ParseTerm();
        while (true)
        {
            SkipWhitespace();
            if (Match('+'))
            {
                value += ParseTerm();
            }
            else if (Match('-'))
            {
                value -= ParseTerm();
            }
            else
            {
                return value;
            }
        }
    }

    private double ParseTerm()
    {
        double value = ParseFactor();
        while (true)
        {
            SkipWhitespace();
            if (Match('*'))
            {
                value *= ParseFactor();
            }
            else if (Match('/'))
            {
                double divisor = ParseFactor();
                if (divisor == 0)
                {
                    throw new DivideByZeroException("Деление на ноль.");
                }

                value /= divisor;
            }
            else
            {
                return value;
            }
        }
    }

    private double ParseFactor()
    {
        SkipWhitespace();
        if (Match('+'))
        {
            return ParseFactor();
        }

        if (Match('-'))
        {
            return -ParseFactor();
        }

        if (Match('('))
        {
            double value = ParseExpression();
            SkipWhitespace();
            if (!Match(')'))
            {
                throw new FormatException("Не закрыта скобка.");
            }

            return value;
        }

        return ParseNumber();
    }

    private double ParseNumber()
    {
        SkipWhitespace();
        int start = _position;
        while (_position < _expression.Length && char.IsAsciiDigit(_expression[_position]))
        {
            _position++;
        }

        if (_position < _expression.Length && _expression[_position] == '.')
        {
            _position++;
            while (_position < _expression.Length && char.IsAsciiDigit(_expression[_position]))
            {
                _position++;
            }
        }

        if (start == _position ||
            !double.TryParse(
                _expression.AsSpan(start, _position - start),
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out double value))
        {
            throw new FormatException("Ожидалось число.");
        }

        return value;
    }

    private void SkipWhitespace()
    {
        while (_position < _expression.Length && char.IsWhiteSpace(_expression[_position]))
        {
            _position++;
        }
    }

    private bool Match(char expected)
    {
        if (_position >= _expression.Length || _expression[_position] != expected)
        {
            return false;
        }

        _position++;
        return true;
    }
}
