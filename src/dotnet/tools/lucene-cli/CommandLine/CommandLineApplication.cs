﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Console = Lucene.Net.Util.SystemConsole;

namespace Lucene.Net.Cli.CommandLine
{
    /*
     * Licensed to the Apache Software Foundation (ASF) under one or more
     * contributor license agreements.  See the NOTICE file distributed with
     * this work for additional information regarding copyright ownership.
     * The ASF licenses this file to You under the Apache License, Version 2.0
     * (the "License"); you may not use this file except in compliance with
     * the License.  You may obtain a copy of the License at
     *
     *     http://www.apache.org/licenses/LICENSE-2.0
     *
     * Unless required by applicable law or agreed to in writing, software
     * distributed under the License is distributed on an "AS IS" BASIS,
     * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
     * See the License for the specific language governing permissions and
     * limitations under the License.
     */

    [SuppressMessage("Performance", "IDE0079:Remove unnecessary suppression", Justification = "This is a SonarCloud issue")]
    [SuppressMessage("Performance", "S3887:Use an immutable collection or reduce the accessibility of the non-private readonly field", Justification = "This was part of the original design")]
    [SuppressMessage("Performance", "S2386:Use an immutable collection or reduce the accessibility of the public static field", Justification = "This was part of the original design")]
    public class CommandLineApplication
    {
        // Indicates whether the parser should throw an exception when it runs into an unexpected argument.
        // If this field is set to false, the parser will stop parsing when it sees an unexpected argument, and all
        // remaining arguments, including the first unexpected argument, will be stored in RemainingArguments property.
        private readonly bool _throwOnUnexpectedArg;

        public CommandLineApplication(bool throwOnUnexpectedArg = true)
        {
            _throwOnUnexpectedArg = throwOnUnexpectedArg;
            Options = new List<CommandOption>();
            Arguments = new List<CommandArgument>();
            Commands = new List<CommandLineApplication>();
            RemainingArguments = new List<string>();
            Invoke = () => 0;
        }

        public CommandLineApplication Parent { get; set; }
        public virtual string Name { get; set; }
        public string FullName { get; set; }
        public string Syntax { get; set; }
        public virtual string Description { get; set; }
        public bool ShowInHelpText { get; set; } = true;
        public string ExtendedHelpText { get; set; }
        public readonly List<CommandOption> Options;
        public CommandOption OptionHelp { get; private set; }
        public CommandOption OptionVersion { get; private set; }
        public readonly List<CommandArgument> Arguments;
        public readonly List<string> RemainingArguments;
        public bool IsShowingInformation { get; protected set; }  // Is showing help or version?
        public Func<int> Invoke { get; set; }
        public Func<string> LongVersionGetter { get; set; }
        public Func<string> ShortVersionGetter { get; set; }
        public readonly List<CommandLineApplication> Commands;
        public bool AllowArgumentSeparator { get; set; }
        public TextWriter Out { get; set; } = Console.Out;
        public TextWriter Error { get; set; } = Console.Error;

        public IEnumerable<CommandOption> GetOptions()
        {
            var expr = Options.AsEnumerable();
            var rootNode = this;
            while (rootNode.Parent != null)
            {
                rootNode = rootNode.Parent;
                expr = expr.Concat(rootNode.Options.Where(o => o.Inherited));
            }

            return expr;
        }

        public virtual CommandLineApplication Command(string name, Action<CommandLineApplication> configuration,
            bool throwOnUnexpectedArg = true)
        {
            var command = new CommandLineApplication(throwOnUnexpectedArg) { Name = name, Parent = this };
            Commands.Add(command);
            configuration(command);
            return command;
        }

        public virtual CommandOption Option(string template, string description, CommandOptionType optionType)
            => Option(template, description, optionType, _ => { }, inherited: false);

        public virtual CommandOption Option(string template, string description, CommandOptionType optionType, bool inherited)
            => Option(template, description, optionType, _ => { }, inherited);

        public virtual CommandOption Option(string template, string description, CommandOptionType optionType, Action<CommandOption> configuration)
            => Option(template, description, optionType, configuration, inherited: false);

        public virtual CommandOption Option(string template, string description, CommandOptionType optionType, Action<CommandOption> configuration, bool inherited)
        {
            var option = new CommandOption(template, optionType)
            {
                Description = description,
                Inherited = inherited
            };
            Options.Add(option);
            configuration(option);
            return option;
        }

        public virtual CommandArgument Argument(string name, string description, bool multipleValues = false)
        {
            return Argument(name, description, _ => { }, multipleValues);
        }

        public virtual CommandArgument Argument(string name, string description, Action<CommandArgument> configuration, bool multipleValues = false)
        {
            var lastArg = Arguments.LastOrDefault();
            if (lastArg != null && lastArg.MultipleValues)
            {
                var message = string.Format("The last argument '{0}' accepts multiple values. No more argument can be added.",
                    lastArg.Name);
                throw new InvalidOperationException(message);
            }

            var argument = new CommandArgument { Name = name, Description = description, MultipleValues = multipleValues };
            Arguments.Add(argument);
            configuration(argument);
            return argument;
        }

        public virtual void OnExecute(Func<int> invoke)
        {
            Invoke = invoke;
        }

        public virtual void OnExecute(Func<Task<int>> invoke)
        {
            Invoke = () => invoke().Result;
        }
        public virtual int Execute(params string[] args)
        {
            CommandLineApplication command = this;
            CommandOption option = null;
            IEnumerator<CommandArgument> arguments = null;

            for (var index = 0; index < args.Length; index++)
            {
                var arg = args[index];
                var processed = false;
                if (!processed && option is null)
                {
                    string[] longOption = null;
                    string[] shortOption = null;

                    if (arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        longOption = arg.Substring(2).Split(new[] { ':', '=' }, 2);
                    }
                    else if (arg.StartsWith("-", StringComparison.Ordinal))
                    {
                        shortOption = arg.Substring(1).Split(new[] { ':', '=' }, 2);
                    }
                    else // Look for symbols (such as help option)
                    {
                        var symbolOption = command.GetOptions().SingleOrDefault(opt => string.Equals(opt.SymbolName, arg, StringComparison.Ordinal));
                        if (symbolOption != null)
                        {
                            shortOption = new string[] { symbolOption.SymbolName, "" };
                        }
                    }
                    if (longOption != null)
                    {
                        processed = true;
                        var longOptionName = longOption[0];
                        option = command.GetOptions().SingleOrDefault(opt => string.Equals(opt.LongName, longOptionName, StringComparison.Ordinal));

                        if (option is null)
                        {
                            if (string.IsNullOrEmpty(longOptionName) && !command._throwOnUnexpectedArg && AllowArgumentSeparator)
                            {
                                // skip over the '--' argument separator
                                index++;
                            }

                            HandleUnexpectedArg(command, args, index, argTypeName: "option");
                            break;
                        }

                        // If we find a help/version option, show information and stop parsing
                        if (command.OptionHelp == option)
                        {
                            command.ShowHelp();
                            return 0;
                        }
                        else if (command.OptionVersion == option)
                        {
                            command.ShowVersion();
                            return 0;
                        }

                        if (longOption.Length == 2)
                        {
                            if (!option.TryParse(longOption[1]))
                            {
                                command.ShowHint();
                                throw new CommandParsingException(command, $"Unexpected value '{longOption[1]}' for option '{option.LongName}'");
                            }
                            option = null;
                        }
                        else if (option.OptionType == CommandOptionType.NoValue)
                        {
                            // No value is needed for this option
                            option.TryParse(null);
                            option = null;
                        }
                    }
                    if (shortOption != null)
                    {
                        processed = true;
                        option = command.GetOptions().SingleOrDefault(opt => string.Equals(opt.ShortName, shortOption[0], StringComparison.Ordinal));

                        // If not a short option, try symbol option
                        if (option is null)
                        {
                            option = command.GetOptions().SingleOrDefault(opt => string.Equals(opt.SymbolName, shortOption[0], StringComparison.Ordinal));
                        }

                        if (option is null)
                        {
                            HandleUnexpectedArg(command, args, index, argTypeName: "option");
                            break;
                        }

                        // If we find a help/version option, show information and stop parsing
                        if (command.OptionHelp == option)
                        {
                            command.ShowHelp();
                            return 0;
                        }
                        else if (command.OptionVersion == option)
                        {
                            command.ShowVersion();
                            return 0;
                        }

                        if (shortOption.Length == 2)
                        {
                            if (!option.TryParse(shortOption[1]))
                            {
                                command.ShowHint();
                                throw new CommandParsingException(command, $"Unexpected value '{shortOption[1]}' for option '{option.LongName}'");
                            }
                            option = null;
                        }
                        else if (option.OptionType == CommandOptionType.NoValue)
                        {
                            // No value is needed for this option
                            option.TryParse(null);
                            option = null;
                        }
                    }
                }

                if (!processed && option != null)
                {
                    processed = true;
                    if (!option.TryParse(arg))
                    {
                        command.ShowHint();
                        throw new CommandParsingException(command, $"Unexpected value '{arg}' for option '{option.LongName}'");
                    }
                    option = null;
                }

                if (!processed && arguments is null)
                {
                    var currentCommand = command;
                    foreach (var subcommand in command.Commands)
                    {
                        if (string.Equals(subcommand.Name, arg, StringComparison.OrdinalIgnoreCase))
                        {
                            processed = true;
                            command = subcommand;
                            break;
                        }
                    }

                    // If we detect a subcommand
                    if (command != currentCommand)
                    {
                        processed = true;
                    }
                }
                if (!processed)
                {
                    if (arguments is null)
                    {
                        arguments = new CommandArgumentEnumerator(command.Arguments.GetEnumerator());
                    }
                    if (arguments.MoveNext())
                    {
                        processed = true;
                        arguments.Current.Values.Add(arg);
                    }
                }
                if (!processed)
                {
                    HandleUnexpectedArg(command, args, index, argTypeName: "command or argument");
                    break;
                }
            }

            if (option != null)
            {
                command.ShowHint();
                throw new CommandParsingException(command, $"Missing value for option '{option.LongName}'");
            }

            return command.Invoke();
        }

        // Helper method that adds a help option
        public virtual CommandOption HelpOption(string template)
        {
            // Help option is special because we stop parsing once we see it
            // So we store it separately for further use
            OptionHelp = Option(template, "Show help information", CommandOptionType.NoValue);

            return OptionHelp;
        }

        public virtual CommandOption VersionOption(string template,
            string shortFormVersion,
            string longFormVersion = null)
        {
            if (longFormVersion is null)
            {
                return VersionOption(template, () => shortFormVersion);
            }
            else
            {
                return VersionOption(template, () => shortFormVersion, () => longFormVersion);
            }
        }

        // Helper method that adds a version option
        public virtual CommandOption VersionOption(string template,
            Func<string> shortFormVersionGetter,
            Func<string> longFormVersionGetter = null)
        {
            // Version option is special because we stop parsing once we see it
            // So we store it separately for further use
            OptionVersion = Option(template, "Show version information", CommandOptionType.NoValue);
            ShortVersionGetter = shortFormVersionGetter;
            LongVersionGetter = longFormVersionGetter ?? shortFormVersionGetter;

            return OptionVersion;
        }

        // Show short hint that reminds users to use help option
        public virtual void ShowHint()
        {
            if (OptionHelp != null)
            {
                Out.WriteLine(string.Format("Specify --{0} for a list of available options and commands.", OptionHelp.LongName));
            }
        }

        // Show full help
        public virtual void ShowHelp(string commandName = null)
        {
            for (var cmd = this; cmd != null; cmd = cmd.Parent)
            {
                cmd.IsShowingInformation = true;
            }

            Out.WriteLine(GetHelpText(commandName));
        }

        public virtual string GetHelpText(string commandName = null)
        {
            var headerBuilder = new StringBuilder("Usage:");
            for (var cmd = this; cmd != null; cmd = cmd.Parent)
            {
                headerBuilder.Insert(6, string.Format(" {0}", cmd.Name));
            }

            CommandLineApplication target;

            if (commandName is null || string.Equals(Name, commandName, StringComparison.OrdinalIgnoreCase))
            {
                target = this;
            }
            else
            {
                target = Commands.SingleOrDefault(cmd => string.Equals(cmd.Name, commandName, StringComparison.OrdinalIgnoreCase));

                if (target != null)
                {
                    headerBuilder.AppendFormat(" {0}", commandName);
                }
                else
                {
                    // The command name is invalid so don't try to show help for something that doesn't exist
                    target = this;
                }

            }

            var optionsBuilder = new StringBuilder();
            var commandsBuilder = new StringBuilder();
            var argumentsBuilder = new StringBuilder();

            var arguments = target.Arguments.Where(a => a.ShowInHelpText).ToList();
            if (arguments.Any())
            {
                headerBuilder.Append(" [arguments]");

                argumentsBuilder.AppendLine();
                argumentsBuilder.AppendLine("Arguments:");
                var maxArgLen = arguments.Max(a => a.Name.Length);
                var outputFormat = string.Format("  {{0, -{0}}}{{1}}", maxArgLen + 2);
                foreach (var arg in arguments)
                {
                    argumentsBuilder.AppendFormat(outputFormat, arg.Name, arg.Description);
                    argumentsBuilder.AppendLine();
                }
            }

            var options = target.GetOptions().Where(o => o.ShowInHelpText).ToList();
            if (options.Any())
            {
                headerBuilder.Append(" [options]");

                optionsBuilder.AppendLine();
                optionsBuilder.AppendLine("Options:");
                var maxOptLen = options.Max(o => o.Template.Length);
                var outputFormat = string.Format("  {{0, -{0}}}{{1}}", maxOptLen + 2);
                foreach (var opt in options)
                {
                    optionsBuilder.AppendFormat(outputFormat, opt.Template, opt.Description);
                    optionsBuilder.AppendLine();
                }
            }

            var commands = target.Commands.Where(c => c.ShowInHelpText).ToList();
            if (commands.Any())
            {
                headerBuilder.Append(" [command]");

                commandsBuilder.AppendLine();
                commandsBuilder.AppendLine("Commands:");
                var maxCmdLen = commands.Max(c => c.Name.Length);
                var outputFormat = string.Format("  {{0, -{0}}}{{1}}", maxCmdLen + 2);
                foreach (var cmd in commands.OrderBy(c => c.Name))
                {
                    commandsBuilder.AppendFormat(outputFormat, cmd.Name, cmd.Description);
                    commandsBuilder.AppendLine();
                }

                if (OptionHelp != null)
                {
                    commandsBuilder.AppendLine();
                    commandsBuilder.AppendFormat($"Use \"{target.Name} [command] --{OptionHelp.LongName}\" for more information about a command.");
                    commandsBuilder.AppendLine();
                }
            }

            if (target.AllowArgumentSeparator)
            {
                headerBuilder.Append(" [[--] <arg>...]");
            }

            headerBuilder.AppendLine();

            var nameAndVersion = new StringBuilder();
            nameAndVersion.AppendLine(GetFullNameAndVersion());
            nameAndVersion.AppendLine();

            return nameAndVersion.ToString()
                + headerBuilder.ToString()
                + argumentsBuilder.ToString()
                + optionsBuilder.ToString()
                + commandsBuilder.ToString()
                + (string.IsNullOrEmpty(target.ExtendedHelpText) ? "" : Environment.NewLine + target.ExtendedHelpText);
        }

        public virtual void ShowVersion()
        {
            for (var cmd = this; cmd != null; cmd = cmd.Parent)
            {
                cmd.IsShowingInformation = true;
            }

            Out.WriteLine(FullName);
            Out.WriteLine(LongVersionGetter());
        }

        public virtual string GetFullNameAndVersion()
        {
            return ShortVersionGetter is null ? FullName : string.Format("{0} {1}", FullName, ShortVersionGetter());
        }

        public virtual void ShowRootCommandFullNameAndVersion()
        {
            var rootCmd = this;
            while (rootCmd.Parent != null)
            {
                rootCmd = rootCmd.Parent;
            }

            Out.WriteLine(rootCmd.GetFullNameAndVersion());
            Out.WriteLine();
        }

        private static void HandleUnexpectedArg(CommandLineApplication command, string[] args, int index, string argTypeName) // LUCENENET: CA1822: Mark members as static
        {
            if (command._throwOnUnexpectedArg)
            {
                command.ShowHint();
                throw new CommandParsingException(command, $"Unrecognized {argTypeName} '{args[index]}'");
            }
            else
            {
                // All remaining arguments are stored for further use
                command.RemainingArguments.AddRange(new ArraySegment<string>(args, index, args.Length - index));
            }
        }

        private class CommandArgumentEnumerator : IEnumerator<CommandArgument>
        {
            private readonly IEnumerator<CommandArgument> _enumerator;

            public CommandArgumentEnumerator(IEnumerator<CommandArgument> enumerator)
            {
                _enumerator = enumerator;
            }

            public CommandArgument Current => _enumerator.Current;

            object IEnumerator.Current => Current;

            public void Dispose()
            {
                _enumerator.Dispose();
            }

            public bool MoveNext()
            {
                if (Current is null || !Current.MultipleValues)
                {
                    return _enumerator.MoveNext();
                }

                // If current argument allows multiple values, we don't move forward and
                // all later values will be added to current CommandArgument.Values
                return true;
            }

            public void Reset()
            {
                _enumerator.Reset();
            }
        }
    }
}