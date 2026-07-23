using System;
using System.Collections.Generic;
using System.Linq;

namespace AvatarBuilder;

public sealed class AvatarBuilderStartupOptions
{
	public bool EasyAvatarMode { get; init; }

	public bool OpenAvatarSystem { get; init; }

	public bool StartAvatarLearning { get; init; }

	public bool SkipLoginPrompt { get; init; }

	public string OutputFolder { get; init; } = "";

	public static AvatarBuilderStartupOptions Default { get; } = new AvatarBuilderStartupOptions();

	public static AvatarBuilderStartupOptions Parse(IEnumerable<string>? args)
	{
		if (args == null)
		{
			return Default;
		}
		bool easyAvatarMode = false;
		bool openAvatarSystem = false;
		bool startAvatarLearning = false;
		bool skipLoginPrompt = false;
		string outputFolder = "";
		List<string> list = args.ToList();
		for (int i = 0; i < list.Count; i++)
		{
			string text = list[i];
			if (string.IsNullOrWhiteSpace(text))
			{
				continue;
			}
			if (TrySplitOptionValue(text, out string name, out string value))
			{
				ApplyOption(name, value);
				continue;
			}
			switch (NormalizeName(text))
			{
			case "easy-avatar":
			case "make-avatar":
			case "avatar":
				easyAvatarMode = true;
				openAvatarSystem = true;
				startAvatarLearning = true;
				break;
			case "open-avatar":
			case "open-avatar-system":
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
			case "output":
			case "output-folder":
				if (i + 1 < list.Count)
				{
					outputFolder = list[++i].Trim();
				}
				break;
			}
		}
		return new AvatarBuilderStartupOptions
		{
			EasyAvatarMode = easyAvatarMode,
			OpenAvatarSystem = (openAvatarSystem || easyAvatarMode),
			StartAvatarLearning = (startAvatarLearning || easyAvatarMode),
			SkipLoginPrompt = skipLoginPrompt,
			OutputFolder = outputFolder
		};
		void ApplyOption(string optionName, string optionValue)
		{
			string text2 = NormalizeName(optionName);
			if (text2 != null)
			{
				switch (text2.Length)
				{
				case 6:
				{
					char c = text2[0];
					if (c != 'a')
					{
						if (c != 'o' || !(text2 == "output"))
						{
							return;
						}
						goto IL_012a;
					}
					if (!(text2 == "avatar"))
					{
						return;
					}
					goto IL_0137;
				}
				case 11:
				{
					char c = text2[0];
					if (c != 'e')
					{
						if (c != 'm')
						{
							if (c != 'o' || !(text2 == "open-avatar"))
							{
								return;
							}
							goto IL_016a;
						}
						if (!(text2 == "make-avatar"))
						{
							return;
						}
					}
					else if (!(text2 == "easy-avatar"))
					{
						return;
					}
					goto IL_0137;
				}
				case 13:
					if (!(text2 == "output-folder"))
					{
						return;
					}
					goto IL_012a;
				case 18:
					if (!(text2 == "open-avatar-system"))
					{
						return;
					}
					goto IL_016a;
				case 21:
					if (!(text2 == "start-avatar-learning"))
					{
						return;
					}
					goto IL_0177;
				case 12:
					if (!(text2 == "start-avatar"))
					{
						return;
					}
					goto IL_0177;
				case 17:
					if (!(text2 == "skip-login-prompt"))
					{
						return;
					}
					break;
				case 15:
					if (!(text2 == "no-login-prompt"))
					{
						return;
					}
					break;
				default:
					return;
					IL_0177:
					startAvatarLearning = IsTruthy(optionValue);
					return;
					IL_012a:
					outputFolder = optionValue.Trim();
					return;
					IL_0137:
					easyAvatarMode = IsTruthy(optionValue);
					openAvatarSystem |= easyAvatarMode;
					startAvatarLearning |= easyAvatarMode;
					return;
					IL_016a:
					openAvatarSystem = IsTruthy(optionValue);
					return;
				}
				skipLoginPrompt = IsTruthy(optionValue);
			}
		}
	}

	private static bool TrySplitOptionValue(string arg, out string name, out string value)
	{
		int num = arg.IndexOf('=', StringComparison.Ordinal);
		if (num <= 0)
		{
			name = "";
			value = "";
			return false;
		}
		name = arg.Substring(0, num);
		value = arg.Substring(num + 1);
		return true;
	}

	private static string NormalizeName(string value)
	{
		return value.Trim().TrimStart(new char[2] { '-', '/' }).ToLowerInvariant();
	}

	private static bool IsTruthy(string value)
	{
		if (!value.Equals("false", StringComparison.OrdinalIgnoreCase) && !value.Equals("0", StringComparison.OrdinalIgnoreCase) && !value.Equals("no", StringComparison.OrdinalIgnoreCase))
		{
			return !value.Equals("off", StringComparison.OrdinalIgnoreCase);
		}
		return false;
	}
}
