using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Acme.Tests.BrandKit;

/// <summary>
/// Every caller that resolves a poster canvas must pass the whole brand context.
/// </summary>
/// <remarks>
/// <para>
/// <b>The hazard this exists for.</b> <c>BrandCanvasAssets.ResolveForRender</c>
/// has four parameters and two of them are optional. Optional is correct — a
/// caller that passes no profile gets a poster with the tokens unresolved, and a
/// caller that passes no encoder gets a gap where the QR belongs, both of which
/// are better than a crash. But it means a caller that <i>forgets</i> an
/// argument fails <b>silently</b>. Nothing warns, nothing throws, and the poster
/// simply comes out missing something.
/// </para>
/// <para>
/// That is not hypothetical. The booking QR was wired into the shared cached
/// render on the assumption that every surface went through it. Three did not:
/// <c>export.pdf</c>, share-by-email, and the support diagnostic that exists to
/// explain "the poster in my email is missing things" — which would have
/// reported nothing about the missing code, because an unresolved QR becomes an
/// empty image source and empty sources are filtered out before the probe sees
/// them. The identical mistake had been made before with <c>{{address}}</c>
/// tokens, and the share-by-email call site carries a comment recording it.
/// </para>
/// <para>
/// So this is a source test, like <c>VideoDocumentOwnershipTests</c>. The fault
/// is an argument quietly missing from a call, and the moment it fails is a
/// poster somebody prints. Catching it needs something that reads the call sites
/// themselves.
/// </para>
/// <para>
/// <b>What a failure here means.</b> Not "add the argument to make the test
/// pass" — it means a surface is about to render posters without the pharmacy's
/// details or without its booking code, and someone should decide whether that
/// surface is meant to.
/// </para>
/// </remarks>
public class BrandAssetCallSiteTests
{
    private const string Method = "ResolveForRender";
    private const string Qualified = "BrandCanvasAssets." + Method;

    /// <summary>The file that declares the method, excluded from call scanning.</summary>
    private const string DeclaringFile = "BrandCanvasAssets.cs";

    /// <summary>
    /// Four: canvas, brand kit, profile, QR encoder. Dropping any of the last
    /// three is the silent failure described above.
    /// </summary>
    /// <remarks>
    /// The second was a bare <c>string? logoBlobKey</c> until the booking link
    /// moved onto the brand kit. It became the whole record rather than growing
    /// a fifth parameter — a fifth would have been a fifth thing to forget, and
    /// this test exists because the ones already there were forgotten.
    /// </remarks>
    private const int RequiredArgumentCount = 4;

    /// <summary>Zero-based positions of the three that fail silently.</summary>
    private const int KitArgument = 1;
    private const int ProfileArgument = 2;
    private const int QrArgument = 3;

    /// <summary>
    /// The call sites that existed when this was written. The scan must find at
    /// least this many, because a scanner that matches nothing passes every
    /// assertion below and reports success — which is worse than no test, since
    /// its name still appears in the output.
    /// </summary>
    private const int KnownCallSites = 4;

    [Fact]
    public void Every_caller_passes_the_profile_and_the_qr_encoder()
    {
        var calls = FindCalls();

        calls.Count.Should().BeGreaterThanOrEqualTo(KnownCallSites,
            "the scan found {0} call site(s) under apps/api/src. Fewer than the "
            + "{1} known ones means either a rendering surface was removed — in "
            + "which case lower the constant deliberately — or this test has "
            + "stopped finding anything and is quietly passing",
            calls.Count, KnownCallSites);

        foreach (var call in calls)
        {
            // No braces anywhere in this message, deliberately. FluentAssertions
            // runs the because-string through string.Format, so a literal
            // pharmacy-name token written out here would either need doubling or
            // throw a FormatException — and a test that dies formatting its own
            // failure message tells nobody anything.
            call.ArgumentCount.Should().Be(RequiredArgumentCount,
                "{0} passes {1} argument(s) to {2}. The optional parameters are "
                + "the pharmacy profile and the QR encoder: without the first the "
                + "poster prints the pharmacy-name and address tokens literally, "
                + "and without the second the booking code is a gap. Both fail "
                + "silently — this call site is the only place it can be caught",
                call.Where, call.ArgumentCount, Method);
        }
    }

    [Fact]
    public void No_caller_passes_a_literal_null_for_the_brand_context()
    {
        // Counting four arguments is not enough on its own. `ResolveForRender(
        // canvas, logoKey, null, null)` passes four and reproduces the fault
        // exactly — tokens printed literally, booking code missing — while
        // looking, to a reviewer skimming for the argument count, entirely
        // correct.
        //
        // Passing null is still allowed by the method, and should be: the admin
        // preview and most tests have no profile and no encoder, and a poster
        // with a gap beats a crash. But a *production rendering surface* doing
        // it is a decision, and this is where it gets made out loud.
        foreach (var call in FindCalls())
        {
            call.Argument(KitArgument).Should().NotBe("null",
                "{0} passes a literal null brand kit, so every poster it renders "
                + "loses the pharmacy logo and the booking code at once. This "
                + "argument used to be a logo key and is now the record that "
                + "carries both, which makes a null here twice as expensive and "
                + "no more visible", call.Where);

            call.Argument(ProfileArgument).Should().NotBe("null",
                "{0} passes a literal null profile, so every poster it renders "
                + "prints the pharmacy-name, address and booking tokens as raw "
                + "text to a patient. Four arguments were passed, which is why "
                + "the count assertion did not catch this", call.Where);

            call.Argument(QrArgument).Should().NotBe("null",
                "{0} passes a literal null QR encoder, so the booking code is a "
                + "gap on every poster it renders. If that is intended for this "
                + "surface, say so here rather than leaving it to look like an "
                + "oversight — which is what it was the last three times",
                call.Where);
        }
    }

    [Fact]
    public void No_caller_reaches_the_method_unqualified()
    {
        // The scan above only sees `BrandCanvasAssets.ResolveForRender(`. A
        // `using static` import, or a second class forwarding to it, would put a
        // call somewhere the scan cannot see — and the test would go on passing
        // while the surface it guards rendered posters with no booking code.
        var strays = new List<string>();

        foreach (var file in SourceFiles())
        {
            var name = Path.GetFileName(file);
            if (name == DeclaringFile) continue;

            var text = Code(file);
            for (var i = text.IndexOf(Method + "(", StringComparison.Ordinal); i >= 0;
                 i = text.IndexOf(Method + "(", i + 1, StringComparison.Ordinal))
            {
                var before = text[..i];
                if (!before.EndsWith("BrandCanvasAssets.", StringComparison.Ordinal))
                    strays.Add($"{name}:{LineAt(text, i)}");
            }
        }

        strays.Should().BeEmpty(
            "every call to {0} outside {1} must be written as {2}(...) so the "
            + "call-site scan can see it. An unqualified call is invisible to "
            + "this test, which is the one thing standing between a new "
            + "rendering surface and a poster with no booking code on it. "
            + "Unqualified call(s) found: {3}",
            Method, DeclaringFile, Qualified, string.Join(", ", strays));
    }

    [Fact]
    public void The_declaring_file_holds_a_single_declaration()
    {
        // One declaration, no internal calls. An overload would be reached by
        // callers that this test then measures against the wrong parameter list,
        // and an internal call is a path the scan cannot follow. Neither is
        // forbidden — both should be a decision somebody makes on purpose, which
        // is what a failing test here forces.
        var text = Code(Path.Combine(SourceRoot, "Acme.Application",
            "BrandKit", DeclaringFile));

        var occurrences = 0;
        for (var i = text.IndexOf(Method + "(", StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(Method + "(", i + 1, StringComparison.Ordinal))
            occurrences++;

        occurrences.Should().Be(1,
            "{0} should appear exactly once in {1} — the declaration. {2} "
            + "occurrence(s) means an overload or an internal call, and both "
            + "change what the call-site scan is actually proving",
            Method, DeclaringFile, occurrences);
    }

    private sealed record CallSite(string Where, IReadOnlyList<string> Arguments)
    {
        public int ArgumentCount => Arguments.Count;

        /// <summary>
        /// The argument expression at a position, or "" when the call has no
        /// argument there.
        /// </summary>
        /// <remarks>
        /// Any <c>name:</c> label is stripped first, so <c>qr: null</c> is read
        /// as the null it is. Without this the one spelling somebody reaches for
        /// when they want to be explicit about skipping the QR is the one
        /// spelling that slips past.
        /// </remarks>
        public string Argument(int index)
        {
            if (index >= Arguments.Count) return "";

            var text = Arguments[index];
            var label = NamedArgument.Match(text);
            return label.Success ? text[label.Length..] : text;
        }

        /// <summary>
        /// A leading <c>identifier:</c>. The ternary <c>a ? b : c</c> does not
        /// match, because a colon reached that way has a <c>?</c> before it.
        /// </summary>
        private static readonly Regex NamedArgument =
            new(@"^\s*[A-Za-z_]\w*\s*:\s*", RegexOptions.Compiled);
    }

    private static List<CallSite> FindCalls()
    {
        var calls = new List<CallSite>();

        foreach (var file in SourceFiles())
        {
            var text = Code(file);
            var name = Path.GetFileName(file);

            for (var i = text.IndexOf(Qualified + "(", StringComparison.Ordinal); i >= 0;
                 i = text.IndexOf(Qualified + "(", i + 1, StringComparison.Ordinal))
            {
                var open = i + Qualified.Length;
                calls.Add(new CallSite(
                    $"{name}:{LineAt(text, i)}", ReadArguments(text, open)));
            }
        }

        return calls;
    }

    /// <summary>
    /// The top-level argument expressions of a call, starting at its opening
    /// parenthesis.
    /// </summary>
    /// <remarks>
    /// A real reader rather than a comma count: these calls span lines, and the
    /// arguments include member access, null-conditionals and string literals.
    /// Commas inside nested brackets, strings and character literals do not
    /// separate arguments. Comments are already blanked by <see cref="Code"/>
    /// before this runs, so a comma in a note between two arguments is not a
    /// fifth argument.
    /// </remarks>
    private static List<string> ReadArguments(string text, int openParen)
    {
        var arguments = new List<string>();
        var depth = 0;
        var start = openParen + 1;

        void Take(int end) => arguments.Add(Flatten(text[start..end]));

        for (var i = openParen; i < text.Length; i++)
        {
            var c = text[i];

            if (c == '"') { i = SkipString(text, i); continue; }
            if (c == '\'') { i = SkipChar(text, i); continue; }

            switch (c)
            {
                case '(' or '[' or '{':
                    depth++;
                    continue;

                case ')' or ']' or '}':
                    depth--;
                    if (depth > 0) continue;

                    // An empty argument list is no arguments, not one blank one.
                    if (text[start..i].Trim().Length > 0) Take(i);
                    return arguments;

                case ',' when depth == 1:
                    Take(i);
                    start = i + 1;
                    continue;
            }
        }

        throw new InvalidOperationException(
            $"Unbalanced parentheses reading a {Method} call. The scanner could "
            + "not find the end of the argument list, so this test cannot report "
            + "anything trustworthy about it.");
    }

    /// <summary>Collapses an argument expression onto one line.</summary>
    private static string Flatten(string argument) =>
        string.Join(' ', argument.Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>Returns the index of the closing quote.</summary>
    private static int SkipString(string text, int start)
    {
        // Raw string literal: """...""" — content may contain quotes and
        // backslashes with no escaping at all.
        if (start + 2 < text.Length && text[start + 1] == '"' && text[start + 2] == '"')
        {
            var fence = 0;
            while (start + fence < text.Length && text[start + fence] == '"') fence++;
            var terminator = new string('"', fence);
            var close = text.IndexOf(terminator, start + fence, StringComparison.Ordinal);
            if (close < 0) return text.Length - 1;
            return close + fence - 1;
        }

        // Verbatim: @"..." with "" as the escaped quote. The caller passes the
        // index of the quote, so look back over any $ and @ prefix — both
        // orders are legal, and $@" and @$" mean the same thing.
        var verbatim = false;
        for (var p = start - 1; p >= 0 && (text[p] == '@' || text[p] == '$'); p--)
            if (text[p] == '@') verbatim = true;

        for (var i = start + 1; i < text.Length; i++)
        {
            if (verbatim)
            {
                if (text[i] != '"') continue;
                if (i + 1 < text.Length && text[i + 1] == '"') { i++; continue; }
                return i;
            }

            if (text[i] == '\\') { i++; continue; }
            if (text[i] == '"') return i;
        }

        return text.Length - 1;
    }

    /// <summary>Returns the index of the closing apostrophe.</summary>
    private static int SkipChar(string text, int start)
    {
        for (var i = start + 1; i < text.Length; i++)
        {
            if (text[i] == '\\') { i++; continue; }
            if (text[i] == '\'') return i;
        }

        return text.Length - 1;
    }

    /// <summary>1-based line number of a character offset.</summary>
    private static int LineAt(string text, int offset) =>
        text.Take(offset).Count(c => c == '\n') + 1;

    /// <summary>
    /// A file's source with every comment blanked out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Comments are replaced with spaces rather than removed, so every offset —
    /// and therefore every line number in a failure message — still points at
    /// the real file.
    /// </para>
    /// <para>
    /// This is not tidiness. <c>BrandCanvasAssets.cs</c> and all four call sites
    /// are heavily commented, precisely because this is the fault they keep
    /// making, and several of those comments name the method. Without this a
    /// sentence in a doc comment would fail the build, and a test that cries
    /// wolf is one people delete.
    /// </para>
    /// </remarks>
    private static string Code(string path)
    {
        var text = File.ReadAllText(path);
        var code = new System.Text.StringBuilder(text.Length);

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                var eol = text.IndexOf('\n', i);
                if (eol < 0) eol = text.Length;
                code.Append(' ', eol - i);
                i = eol - 1;
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                // Clamped, because a file ending in a bare "/*" would otherwise
                // index past the end — a crash instead of a test result.
                var from = Math.Min(i + 2, text.Length);
                var end = text.IndexOf("*/", from, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end + 2;

                // Newlines are kept so line numbers do not shift.
                for (var j = i; j < end; j++)
                    code.Append(text[j] == '\n' ? '\n' : ' ');

                i = end - 1;
                continue;
            }

            if (c is '"' or '\'')
            {
                var close = c == '"' ? SkipString(text, i) : SkipChar(text, i);
                code.Append(text, i, close - i + 1);
                i = close;
                continue;
            }

            code.Append(c);
        }

        return code.ToString();
    }

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(SourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .OrderBy(p => p, StringComparer.Ordinal);

    private static readonly string SourceRoot = FindSourceRoot();

    /// <summary>Walks up from the test binary to <c>apps/api/src</c>.</summary>
    private static string FindSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src");
            if (Directory.Exists(Path.Combine(candidate, "Acme.Application")))
                return candidate;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not find apps/api/src from the test output directory. This "
            + "test reads call sites out of the sources, so it needs the tree "
            + "rather than just the compiled assemblies.");
    }
}
