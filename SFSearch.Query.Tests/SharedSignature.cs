// A record type that shares its signature with others (GMST's game settings, OMOD's object modifications): the tests
// that hold the byte reads against Mutagen's object (typed-all, lists-all) hold only the type's own records, and
// with every record of the signature what its bytes say it is (SFSearch.Records.RecordClasses) against the class
// Mutagen made.
using SFSearch.Index;
using SFSearch.Records;

namespace SFSearch.Query.Tests;

sealed class SharedSignature
{
    readonly Func<RawRecord, string?>? _classOf;
    long _said, _unsaid;

    SharedSignature(Func<RawRecord, string?>? classOf) => _classOf = classOf;

    /// <summary>For a type whose signature other types share; null for a type with a signature of its own.</summary>
    public static SharedSignature? Of(Ctx c, string type) =>
        c.Schema.SignatureOf(type) is { } sig && c.Schema.TypesOf(sig).Count > 1 ? new SharedSignature(RecordClasses.For(c.Schema, sig)) : null;

    /// <summary>The records of the signature whose type the bytes said, and those left to Mutagen's read.</summary>
    public (long Said, long Unsaid) Counts => (Interlocked.Read(ref _said), Interlocked.Read(ref _unsaid));

    /// <summary>Whether the record is one of <paramref name="type"/>, by the class Mutagen made (<paramref name="made"/>).
    /// What the bytes say it is must be that class, or nothing: a difference is added to <paramref name="output"/>.</summary>
    public bool Mine(Ctx c, in PipelineRecord r, string made, string type, List<string> output)
    {
        string? cls = _classOf?.Invoke(RawRecord.FromInflated(r.Head, r.Data));
        if (cls == null) Interlocked.Increment(ref _unsaid);
        else
        {
            Interlocked.Increment(ref _said);
            if (cls != made) { output.Add($"{c.Index.Describe(r.Ref).Label}: its bytes say it is a {cls}, Mutagen made a {made}"); return false; }
        }
        return made == type;
    }
}
