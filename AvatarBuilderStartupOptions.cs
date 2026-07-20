namespace AvatarBuilder;

public sealed class AvatarBuilderStartupOptions
{
    public bool EasyAvatarMode { get; init; }

    public bool OpenAvatarSystem { get; init; }

    public bool StartAvatarLearning { get; init; }

    public bool SkipLoginPrompt { get; init; }

    public string OutputFolder { get; init; } = "";

    public static AvatarBuilderStartupOptions Default { get; } = new();

    public static AvatarBuilderStartupOptions Parse(IEnumerable<string>? args)
    {
        if (args is null)
        {
            return Default;
        }

        var easyAvatarMode = false;
        var openAvatarSystem = false;
        var startAvatarLearning = false;
        var skipLoginPrompt = false;
        var outputFolder = "";
        var values = args.ToList();
        for (var index = 0; index < values.Count; index++)
        {
            var arg = values[index];
            if (string.IsNullOrWhiteSpace(arg))
            {
                continue;
            }

            if (TrySplitOptionValue(arg, out var name, out var inlineValue))
            {
                ApplyOption(name, inlineValue);
                continue;
            }

            switch (NormalizeName(arg))
            {
                case "easy-avatar":
                case "avatar":
                case "make-avatar":
                    easyAvatarMode = true;
                    openAvatarSystem = true;
                    startAvatarLearning = true;
                    break;
                case "open-avatar-system":
                case "open-avatar":
                    openAvatarSystem = true;
                    break;
                case "start-avatar-learning":
                case "start-avatar":
                    startAvatarLearning = true;
                    break;
                case "skip-login-prompt":
                case "no-login-prompt":
                    skipLoginPrompt = true;
                    break;
                case "output-folder":
                case "output":
                    if (index + 1 < values.Count)
                    {
                        outputFolder = values[++index].Trim();
                    }
                    break;
            }
        }

        return new AvatarBuilderStartupOptions
        {
            EasyAvatarMode = easyAvatarMode,
            OpenAvatarSystem = openAvatarSystem || easyAvatarMode,
            StartAvatarLearning = startAvatarLearning || easyAvatarMode,
            SkipLoginPrompt = skipLoginPrompt,
            OutputFolder = outputFolder
        };

        void ApplyOption(string optionName, string optionValue)
        {
            switch (NormalizeName(optionName))
            {
                case "output-folder":
                case "output":
                    outputFolder = optionValue.Trim();
                    break;
                case "easy-avatar":
                case "avatar":
                case "make-avatar":
                    easyAvatarMode = IsTruthy(optionValue);
                    openAvatarSystem = openAvatarSystem || easyAvatarMode;
                    startAvatarLearning = startAvatarLearning || easyAvatarMode;
                    break;
                case "open-avatar-system":
                case "open-avatar":
                    openAvatarSystem = IsTruthy(optionValue);
                    break;
                case "start-avatar-learning":
                case "start-avatar":
                    startAvatarLearning = IsTruthy(optionValue);
                    break;
                case "skip-login-prompt":
                case "no-login-prompt":
                    skipLoginPrompt = IsTruthy(optionValue);
                    break;
            }
        }
    }

    private static bool TrySplitOptionValue(string arg, out string name, out string value)
    {
        var separator = arg.IndexOf('=', StringComparison.Ordinal);
        if (separator <= 0)
        {
            name = "";
            value = "";
            return false;
        }

        name = arg[..separator];
        value = arg[(separator + 1)..];
        return true;
    }

    private static string NormalizeName(string value)
    {
        return value.Trim().TrimStart('-', '/').ToLowerInvariant();
    }

    private static bool IsTruthy(string value)
    {
        return !value.Equals("false", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("0", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("no", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("off", StringComparison.OrdinalIgnoreCase);
    }
}
