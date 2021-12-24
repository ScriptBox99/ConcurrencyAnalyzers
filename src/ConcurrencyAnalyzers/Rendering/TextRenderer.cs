﻿using System;
using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.IO;
using static ConcurrencyAnalyzers.FragmentFactory;

namespace ConcurrencyAnalyzers;

public enum FragmentKind
{
    Border,
    Header,
    ExceptionType,
    ExceptionMessage,
    StackFrame,
    Namespace,
    TypeName,
    MethodName,
    Separator,
    Text,
    Argument,
    ArgumentModifier,
}

public record OutputFragment(FragmentKind Kind, string Text);

public record OutputLine(OutputFragment[] Fragments);

internal static class FragmentFactory
{
    public static OutputFragment Separator(string text) => new OutputFragment(FragmentKind.Separator, text);
    public static OutputFragment Border(string text) => new OutputFragment(FragmentKind.Border, text);
    public static OutputFragment Text(string text) => new OutputFragment(FragmentKind.Text, text);
    public static OutputFragment Fragment(FragmentKind kind, string text) => new OutputFragment(kind, text);
}

public class TextRenderer : IDisposable
{
    private const int MaxWidth = 100;
    private const int BorderWidth = 2; // '| '
    private readonly int _maxWidth;
    private readonly string _separatorLine;

    protected readonly TextWriter Writer;
    private readonly bool _renderRawStackFrames;

    public TextRenderer(TextWriter writer, bool renderRawStackFrames = false, int maxWidth = MaxWidth)
    {
        Writer = writer;
        _renderRawStackFrames = renderRawStackFrames;
        _maxWidth = maxWidth;
        _separatorLine = new string('-', _maxWidth);
    }

    public virtual void Dispose() { }

    public void Render(ParallelThreads parallelThreads)
    {
        RenderOverview(parallelThreads);
        foreach (var thread in parallelThreads.GroupedThreads)
        {
            RenderThread(thread);
        }
    }

    private void RenderOverview(ParallelThreads parallelThreads)
    {
        RenderLineSeparator();
        RenderLine(
            Text($"Thread count: {parallelThreads.ThreadCount} "),
            Text($"Unique stack traces: {parallelThreads.GroupedThreads.Length}"));
        RenderLineSeparator();
    }

    protected virtual void RenderLine(params OutputFragment[] fragments)
    {
        Contract.Requires(fragments.Length != 0);

        RenderLine(new OutputLine(fragments));
    }

    protected virtual void RenderLine(OutputLine line)
    {
        int currentLineWidth = RenderFragment(Border("| "));

        for (var index = 0; index < line.Fragments.Length; index++)
        {
            int fragmentWidth = line.Fragments[index].Text.Length;

            if (currentLineWidth + fragmentWidth + BorderWidth > _maxWidth && currentLineWidth != BorderWidth)
            {
                // Need to break the line! But covering the case if the fragment itself is way too wide.
                RenderClosingBorder(currentLineWidth);

                // Adding a few extra spaces to separate the line break.
                currentLineWidth = RenderFragment(Border("|    "));
            }

            var fragment = line.Fragments[index];
            currentLineWidth += RenderFragment(fragment);
        }

        RenderClosingBorder(currentLineWidth);
    }

    private void RenderClosingBorder(int currentTextLength)
    {
        var extraSpacesCount = currentTextLength < _maxWidth ? _maxWidth - currentTextLength : 0;
        RenderFragment(Border($"{new string(' ', extraSpacesCount)} |"));
        RenderNewLine();
    }

    protected static OutputFragment Fragment(FragmentKind kind, string text) => new OutputFragment(kind, text);

    public virtual int RenderFragment(OutputFragment fragment)
    {
        Writer.Write(fragment.Text);
        return fragment.Text.Length;
    }

    public virtual void RenderNewLine() => Writer.WriteLine();

    protected void RenderLineSeparator()
    {
        RenderFragment(Border($"|{_separatorLine}|"));
        RenderNewLine();
    }

    protected virtual void RenderThread(ParallelThread thread)
    {
        RenderHeader(thread);
        RenderExtraThreadInfo(thread);
        RenderStackFrames(thread);

        if (_renderRawStackFrames)
        {
            RenderLineSeparator();
            RenderLine(Text("Raw stack frames:"));
            RenderRawStackFrames(thread);
        }

        RenderLineSeparator();
    }

    protected virtual void RenderExtraThreadInfo(ParallelThread thread)
    {
        var fragments = new List<OutputFragment>();
        if (thread is SingleParallelThread spt && spt.ThreadInfo.Exception is var exception && exception is not null)
        {
            fragments.Add(
                new OutputFragment(FragmentKind.ExceptionType, exception.TypeName));
            
            if (exception.Message is not null)
            {
                fragments.Add(Separator(": "));
                fragments.Add(new OutputFragment(FragmentKind.ExceptionMessage, exception.Message));
            }
        }

        if (!thread.ThreadInfo.LockCount.IsEmpty)
        {
            fragments.Add(Fragment(FragmentKind.TypeName, "LockCount"));
            fragments.Add(Separator(": "));
            fragments.Add(Fragment(FragmentKind.MethodName, thread.ThreadInfo.LockCount.ToString()));
        }

        if (fragments.Count > 0)
        {
            RenderLine(fragments.ToArray());
            RenderLineSeparator();
        }
    }

    protected virtual void RenderHeader(ParallelThread thread)
    {
        RenderLineSeparator();
        RenderLine(Fragment(FragmentKind.Header, thread.Header));
        RenderLineSeparator();
    }

    protected virtual void RenderStackFrames(ParallelThread thread)
    {
        foreach (var stackFrame in thread.ThreadInfo.StackFrames)
        {
            RenderStackFrame(stackFrame);
        }
    }

    private void RenderRawStackFrames(ParallelThread thread)
    {
        foreach (var stackFrame in thread.ThreadInfo.RawStackFrames)
        {
            var fragments = new List<OutputFragment>();
            fragments.AddRange(TokenizeFullName(stackFrame, FragmentKind.TypeName, FullNameSeparators));
            RenderLine(fragments.ToArray());
        }
    }

    private static readonly char[] FullNameSeparators = new char[] { '.', '<', '>', ',' };

    protected virtual void RenderStackFrame(StackFrame stackFrame)
    {
        var fragments = new List<OutputFragment>();
        fragments.AddRange(TokenizeFullName(stackFrame.TypeName, FragmentKind.TypeName, FullNameSeparators));
        fragments.Add(Separator("."));
        
        fragments.AddRange(TokenizeFullName(stackFrame.Method, FragmentKind.MethodName, FullNameSeparators));
        fragments.Add(Separator("("));
        fragments.AddRange(TokenizeArguments(stackFrame.Arguments, FullNameSeparators));
        fragments.Add(Separator(")"));

        RenderLine(fragments.ToArray());
    }

    private List<OutputFragment> TokenizeFullName(ReadOnlySpan<char> typeOrMethodName, FragmentKind fragmentKind, char[] nameSeparators)
    {
        var fragments = new List<OutputFragment>();
        StackFrameTokenizer.TokenizeTypeOrMethodName(
            typeOrMethodName, 
            nameSeparators,
            (tuple) =>
            {
                var (token, isSeparator) = tuple;

                fragments.Add(Fragment(isSeparator ? FragmentKind.Separator : fragmentKind, token));
            });

        return fragments;
    }

    private List<OutputFragment> TokenizeArguments(ReadOnlySpan<char> arguments, char[] nameSeparators)
    {
        var fragments = new List<OutputFragment>();
        StackFrameTokenizer.TokenizeArgumentList(
            arguments,
            nameSeparators,
            (tuple) =>
            {
                var (token, isModifier, isSeparator) = tuple;
                FragmentKind fragmentKind = (isSeparator, isModifier) switch
                {
                    (true, _) => FragmentKind.Separator,
                    (_, true) => FragmentKind.ArgumentModifier,
                    _ => FragmentKind.Argument,
                };

                fragments.Add(new OutputFragment(fragmentKind, token));
            });

        return fragments;
    }
}