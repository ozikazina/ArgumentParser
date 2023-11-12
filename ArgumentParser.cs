using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Arguments.ArgumentParser
{
	/// <summary>
	/// Class capable of parsing properties of the generic type <typeparamref name="TArgs"/> from the command line.<br/>
	/// When the arguments <c>-h</c> or <c>--help</c> are present, a help print-out is generated based on set attributes.
	/// </summary>
	/// <remarks>
	/// Allowed boolean values are on|true|yes|1 for <see langword="true"/> and off|false|no|not|0 for <see langword="false"/>.<br/>
	/// To set a boolean value based on the presence of an option, use the <see cref="Flag"/> type.<br/>
	/// Windows notation of /f is automatically converted to -f.
	/// </remarks>
	/// <typeparam name="TArgs">Class containing writeable properties with the <see cref="ArgumentAttribute"/> set. Can optionally have the <see cref="ProjectAttribute"/>.</typeparam>
	public partial class ArgumentParser<TArgs> where TArgs : notnull, new()
	{
		static ProjectAttribute? ProjectInfo;
		static List<(PropertyInfo Property, ArgumentAttribute Attribute)> Properties;
		static List<(PropertyInfo Property, ArgumentAttribute Attribute)> RequiredProperties;
		static Dictionary<string, int> ArgumentMap;

		[GeneratedRegex(@"on|true|yes|1", RegexOptions.IgnoreCase)]
		private static partial Regex BoolOnRegex();

		[GeneratedRegex(@"off|false|not?|0", RegexOptions.IgnoreCase)]
		private static partial Regex BoolOffRegex();

		/// <summary>
		/// Default constructor for the <see cref="ArgumentParser{TArgs}"/> class.
		/// </summary>
		public ArgumentParser()
        {
            
        }

        static ArgumentParser()
		{
			ProjectInfo = typeof(TArgs).GetCustomAttribute(typeof(ProjectAttribute)) as ProjectAttribute;

			Properties = typeof(TArgs).GetProperties()
				.Where(x => x.CanWrite)
				.Where(x => x.GetCustomAttribute(typeof(ArgumentAttribute)) != null)
				.Select(x => (x, (x.GetCustomAttribute(typeof(ArgumentAttribute)) as ArgumentAttribute)!))
				.ToList();

			if (Properties.Count == 0)
			{
				ArgumentMap = new();
				RequiredProperties = new();
			}
			else
			{
				ArgumentMap = Properties
					.SelectMany((x, i) => x.Attribute.Options.Select(y => (Index: i, Option: y)))
					.ToDictionary(x => x.Option, x => x.Index);

				RequiredProperties = Properties.Where(x => x.Attribute.Options.Count == 0).ToList();

				foreach (var pa in Properties)
				{
					if (pa.Attribute.CountMentions)
					{
						if (pa.Property.PropertyType != typeof(int))
							throw new ArgumentException($"Property {pa.Property.Name} has to be an int type for Argument.CountMentions to work.");
						if (pa.Attribute.Options.Count == 0)
							throw new ArgumentException($"ArgumentAttribute of {pa.Property.Name} has to specify option names for Argument.CountMentions to work.");
					}
				}
			}
		}

		private void PrintHelp()
		{
			if (ProjectInfo != null)
			{
				var name = Assembly.GetEntryAssembly()?.GetName();
				Console.WriteLine(ProjectInfo.Name ?? name?.Name ?? "Name");
				string? version = ProjectInfo.Version ?? name?.Version?.ToString();
				if (version != null) Console.WriteLine($"Version: {version}");
				Console.WriteLine();
				Console.WriteLine(ProjectInfo.Description);
				Console.WriteLine();
			}

			if (Properties.Count > 0)
			{
				Console.WriteLine("--- Command line options:");
				var longestName = Properties.Select(x => x.Attribute.Name?.Length ?? x.Property.Name.Length).Max();
				foreach ((var prop, var attr) in Properties)
				{
					Console.Write(": ");
					Console.Write((attr.Name ?? prop.Name).PadRight(longestName));
					if (attr.Options.Count != 0)
					{
						Console.WriteLine($" ({string.Join(", ", attr.Options)})");
					}
					else
					{
						Console.WriteLine();
					}

					if (attr.Description != null)
					{
						Console.Write("| ");
						Console.WriteLine(attr.Description);
					}
					Console.WriteLine();
				}
			}
			else
			{
				Console.WriteLine("No options available.");
			}

			if (ProjectInfo != null && ProjectInfo.Addendum != null)
			{
				Console.WriteLine("--- Also:");
				Console.WriteLine(ProjectInfo.Addendum);

			}
		}

		private bool? TryParseBool(string arg)
		{
			if (BoolOnRegex().IsMatch(arg))
			{
				return true;
			}
			else if (BoolOffRegex().IsMatch(arg))
			{
				return false;
			}
			else
			{
				Console.Error.WriteLine($"Failed to parse boolean value: {arg}");
				return null;
			}
		}

		private object? TryParseEnum(string arg, Type enumType)
		{
			if (int.TryParse(arg, out int enumIndex))
			{
				if (Enum.IsDefined(enumType, enumIndex))
				{
					return Enum.ToObject(enumType, enumIndex);
				}
				else
				{
					Console.Error.WriteLine($"Specified value is out of range: {enumIndex}");
					Console.Error.WriteLine($"[Allowed values are: {string.Join(',', Enum.GetValues(enumType))}]");
				}
			}
			else if (Enum.TryParse(enumType, arg, out object? result))
			{
				return result;
			}
			else
			{
				Console.Error.WriteLine($"Value not recognized: {arg}");
				Console.Error.WriteLine($"[Allowed values are: {string.Join(',', Enum.GetNames(enumType))}]");
			}
			return null;
		}

		public ParsedArguments<TArgs> Parse(string[] arguments)
		{
			if (arguments.Any(x => x == "-h" || x == "--help"))
			{
				PrintHelp();
				return new() { Arguments = new(), Success = true, PrintedHelp = true };
			}

			var ret = new TArgs();

			List<string> args = new();

			foreach (var arg in arguments)	//breaks apart compact options
			{
				if (arg.Length > 1 && (arg[0] == '-' || arg[0] == '/') && arg[1] != '-')
				{
					args.AddRange(arg.Skip(1).Select(opt => $"-{opt}"));
				}
				else if (arg.Length > 1 && arg[0] == '/')
				{
					args.Add($"-{arg[1..]}");
				}
				else
				{
					args.Add(arg);
				}
			}

			ArgumentError errors = ArgumentError.None;
			int indexRequired = 0;

			for (int i = 0; i < args.Count; i++)
			{
				PropertyInfo Property;
				ArgumentAttribute Attribute;
				string argName;

				if (ArgumentMap.TryGetValue(args[i], out int index))	//optional arg
				{
					Property = Properties[index].Property;
					Attribute = Properties[index].Attribute;
					argName = args[i];
					if (Property.PropertyType == typeof(Flag))
					{
						Property.SetValue(ret, new Flag(true));
						continue;
					}
					if (Attribute.CountMentions)
					{
						Property.SetValue(ret, ((int)Property.GetValue(ret)!) + 1);
						continue;
					}
					i++;
					if (i == args.Count)
					{
						Console.Error.WriteLine($"Missing value for: {argName}");
						errors |= ArgumentError.MissingValue;
						break;
					}
					if (ArgumentMap.ContainsKey(args[i]))
					{
						Console.Error.WriteLine($"Missing value for: {argName}, encountered next option instead.");
						i--;
						errors |= ArgumentError.MissingValue;
						continue;
					}
				}
				else if (indexRequired < RequiredProperties.Count)	//required arg
				{
					Property = RequiredProperties[indexRequired].Property;
					Attribute = RequiredProperties[indexRequired].Attribute;
					argName = Attribute.Name ?? Property.Name;
					++indexRequired;
				}
				else //error
				{
					Console.Error.WriteLine($"Argument not recognized: {args[i]}");
					errors |= ArgumentError.WrongValue;
					continue;
				}

				if (Property.PropertyType.IsArray)
				{
					var type = Property.PropertyType.GetElementType()!;
					bool isBool = type == typeof(bool);
					bool isEnum = type.IsEnum;

					var maxCnt = Attribute.Length > 0 ? Attribute.Length : int.MaxValue;
					maxCnt = Math.Min(maxCnt, args.Count - i);

					List<object> retyped = new();
					
					for (int j = 0; j < maxCnt; j++, i++)
					{
						if (ArgumentMap.ContainsKey(args[i]))
						{
							i--;
							break;
						}

						if (isBool)
						{
							var val = TryParseBool(args[i]);
							if (val.HasValue) retyped.Add(val.Value);
							else errors |= ArgumentError.WrongValue;
						}
						else if (isEnum)
						{
							var val = TryParseEnum(args[i], type);
							if (val != null) retyped.Add(val);
							else errors |= ArgumentError.WrongValue;
						}
						else
						{
							try
							{
								retyped.Add(Convert.ChangeType(args[i], type, CultureInfo.InvariantCulture));
							}
							catch
							{
								Console.Error.WriteLine($"Argument couldn't be parsed: {args[i]}");
								errors |= ArgumentError.WrongValue;
							}
						}
					}

					if (Attribute.NonEmpty && retyped.Count == 0)
					{
						Console.Error.WriteLine($"Argument {argName} can't be empty.");
						errors |= ArgumentError.EmptyArray;
					}
					else
					{
						var ar = Array.CreateInstance(type, retyped.Count);
						for (int j = 0; j < retyped.Count; j++)
							ar.SetValue(retyped[j], j);
						Property.SetValue(ret, ar);
					}
				}
				else if (Property.PropertyType.IsEnum)
				{
					var val = TryParseEnum(args[i], Property.PropertyType);
					if (val != null) Property.SetValue(ret, val);
					else errors |= ArgumentError.WrongValue;
				}
				else if (Property.PropertyType == typeof(bool))
				{
					var val = TryParseBool(args[i]);
					if (val.HasValue) Property.SetValue(ret, val.Value);
					else errors |= ArgumentError.WrongValue;
				}
				else
				{
					try
					{
						Property.SetValue(ret, Convert.ChangeType(args[i], Property.PropertyType, CultureInfo.InvariantCulture));
					}
					catch
					{
						Console.Error.WriteLine($"Failed to parse: {args[i]}");
						errors |= ArgumentError.WrongValue;
					}
				}
			}

			return new ParsedArguments<TArgs>()
			{
				Success = errors == ArgumentError.None,
				Errors = errors,
				Arguments = ret
			};
		}
	}

	/// <summary>
	/// Class wrapping parsed arguments. Includes additional data from the parsing process.
	/// </summary>
	/// <typeparam name="TArgs">Type of the class of argument properties being processed.</typeparam>
	public class ParsedArguments<TArgs> where TArgs : notnull
	{
		/// <summary>
		/// <c>true</c> if parsing succeeded without errors.
		/// </summary>
		public bool Success { get; init; }
		/// <summary>
		/// <c>true</c> if help was printed. No additional arguments were processed.
		/// </summary>
		public bool PrintedHelp { get; init; }
		/// <summary>
		/// Flag enumeration of encountered errors, if there were any. 
		/// </summary>
		public ArgumentError Errors { get; init; }
		/// <summary>
		/// Parsed arguments.
		/// </summary>
		public required TArgs Arguments { get; init; }
	}

	/// <summary>
	/// Attribute for properties of classes passed to <see cref="ArgumentParser{T}"/>.<br/>
	/// Properties with this attribute are parsed and set when processing command line arguments.
	/// </summary>
	/// <remarks>
	/// Types are determined from the property type, allowing for <c>number</c> and <see langword="string"/> types, <see cref="bool"/>, <see langword="enum"/>, <see cref="Flag"/>, and their respective <c>arrays</c>.<br/>
	/// Properties with this attribute have to be public and writeable.
	/// </remarks>
	[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
	public partial class ArgumentAttribute : Attribute
	{
		public HashSet<string> Options;
		/// <summary>
		/// Name to display on print-out. Property name is used otherwise.
		/// </summary>
		public string? Name;
		/// <summary>
		/// Argument description to display on print-out.
		/// </summary>
		public string? Description;
		/// <summary>
		/// Specifies, that an Array type can't be empty.
		/// </summary>
		public bool NonEmpty = false;
		/// <summary>
		/// Saves how many times this argument was specified.
		/// </summary>
		/// <remarks>Only for <see cref="int"/> properties. Argument has to have option names specified.</remarks>
		public bool CountMentions;
		/// <summary>
		/// Specific length for Array types.
		/// </summary>
		public int Length = 0;
		/// <summary>
		/// Makes the argument mandatory, even if it has option names specified.
		/// </summary>
		public bool Mandatory;

		[GeneratedRegex(@"^(-\w|--\w+)$", RegexOptions.ECMAScript | RegexOptions.ExplicitCapture)]
		private static partial Regex ValidationRegex();

		/// <summary>
		/// Initializes a new Argument attribute with arbitrary option names.
		/// </summary>
		/// <remarks>
		/// <example>Usage example:
		/// <code>[Argument("-i", "--input", Name="Input", Description="Program input.")]</code>
		/// </example>
		/// Specified <paramref name="options"/> have to match <c>-\w|--\w+</c>.<br/>
		/// If none are specified, then the argument is <b>mandatory</b>.<br/>
		/// Specifying option names makes it <b>optional</b>, unless overriden by <see cref="Mandatory"/>.</remarks>
		/// <param name="options">An arbitrary list of option names. Elements have to match <c>-\w|--\w+</c>.
		/// If none are specified, then the argument is <b>mandatory</b>.</param>
		/// <exception cref="ArgumentException">Thrown if duplicate option names or invalid formats are detected.</exception>
		public ArgumentAttribute(params string[] options)
		{
			var set = new HashSet<string>(options);
			if (set.Count != options.Length)
				throw new ArgumentException("Duplicate options specified.");

			foreach (var arg in set.Where(arg => !ValidationRegex().IsMatch(arg)))
			{
				throw new ArgumentException($"Invalid option format: {arg}. Format has to match -\\w|--\\w+");
			}
			
			Options = set;
		}
	}

	/// <summary>
	/// Attribute for classes passed to <see cref="ArgumentParser{T}"/>.<br/>
	/// Allows for further details in help print-outs.
	/// </summary>
	[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
	public class ProjectAttribute : Attribute
	{
		/// <summary>
		/// Project name to display on print-out. Entry assembly name is used otherwise.
		/// </summary>
		public string? Name;
		/// <summary>
		/// Project version to display on print-out. Entry assembly version is used otherwise.
		/// </summary>
		public string? Version;
		/// <summary>
		/// Project requirements to display on print-out.
		/// </summary>
		public string? Requirements;
		/// <summary>
		/// Project description to display on print-out.
		/// </summary>
		public string? Description;
		/// <summary>
		/// Additional remarks printed last to console.
		/// </summary>
		public string? Addendum;
	}

	/// <summary>
	/// Evaluates to <see langword="true"/> if it's flag is passed in the command line.<br/>
	/// Should be used in conjunction with <see cref="ArgumentAttribute"/>.
	/// </summary>
	/// <remarks>
	/// This data type differs from that of <see cref="bool"/>, which can be set to either value in the command line.
	/// </remarks>
	public struct Flag
	{
		private bool Value;

		/// <summary>
		/// Creates a new <c>Flag</c> object with the specified value.
		/// </summary>
		/// <param name="value">Value to set the <c>Flag</c> to.</param>
		public Flag(bool value)
		{
			Value = value;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static implicit operator bool(Flag f) => f.Value;

		public override string ToString() => Value.ToString();
	}

	/// <summary>
	/// Flag enum of possible error types when processing command line arguments.
	/// </summary>
	[Flags]
	public enum ArgumentError
	{
		None = 0,
		MissingValue = 1,
		WrongValue = 2,
		EmptyArray = 4
	}
}