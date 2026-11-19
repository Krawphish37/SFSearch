// The names SQL had before 2.0, each with what answers for it now (agent-docs: references\old-tools.md, "Old SQL").
// The old tables, columns and functions went with the old Query command; SQL written for them (in notes, in an
// agent's memory of earlier sessions) fails on the first old name. The error then says the new one, so the next
// try works (GAPS S207: 15 of 258 replayed old queries stopped at "check the column's name").
namespace SFSearch.Query;

static class OldNames
{
    static readonly Dictionary<string, string> Tables = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Refs"] = "Refs was the old table of links: it is Links now (FromKey, FromType, Field, Path, ToKey), and sfs_refs (refs2) searches every type for what points at a record",
        ["SignatureCounts"] = "SignatureCounts is gone: SELECT Sig, Type, count(*) FROM Forms GROUP BY Sig, Type gives the counts",
        ["Masters"] = "the old Masters table is gone: SELECT Plugin, Masters FROM Plugins, or loadorder2 <plugin>, gives a plugin's masters",
        ["AllRecords"] = "AllRecords (a plugin with its masters attached) is gone: name the plugins (query2 a.esm,b.esm …) or use a scope; edid(fk), name(fk) and type(fk) answer for any record in Data",
        ["sf.Records"] = "sf.Records (the attached Starfield.esm) is gone: name the plugins (query2 Starfield.esm,x.esm …) or use a scope; edid(fk), name(fk) and type(fk) answer for any record in Data",
        ["lo.Winners"] = "lo.Winners is the winners scope now (--scope winners, scope: \"winners\"): every typed view then holds the winning versions",
        ["lo.WinningRecords"] = "lo.WinningRecords is the winners scope now (--scope winners, scope: \"winners\"): every typed view then holds the winning versions",
        ["lo.Versions"] = "lo.Versions is the versions scope now (--scope versions), with LoadOrder and IsWinner on every row; Forms has every record's header",
        ["lo.Plugins"] = "lo.Plugins is the table Plugins now (and loadorder2)",
        ["scr.Scripts"] = "script source isn't a table any more: scripts2 <plugin> --find <text> searches it, scripts2 <plugin> --show <script> shows one",
        ["scr.Lines"] = "script source isn't a table any more: scripts2 <plugin> --find <text> searches it, scripts2 <plugin> --show <script> shows one",
    };

    static readonly Dictionary<string, string> Columns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Signature"] = "the old column Signature is two columns now: Sig (WEAP) and Type (Weapon); a type's own view (FROM Weapon) needs neither",
        ["Data"] = "the old column Data (the record's JSON) is Json now; a field of it is the type's column of that name (AttackDamage), not json_extract(Data, …)",
        ["Origin"] = "the old column Origin is gone: IsOverride says whether the plugin overrides the record (0: its own), and the FormKey's plugin part says where the record is from",
        ["Base"] = "Forms has no Base now: a placed reference's base is PlacedObject.Base (PlacedNpc.Base for a placed actor)",
        ["HasScripts"] = "HasScripts is gone: a type's scripts are the list view <Type>_VirtualMachineAdapter_Scripts (and the stored query scripts-used)",
        ["FromFormKey"] = "FromFormKey is FromKey now (Links: FromKey, FromType, Field, Path, ToKey)",
        ["ToFormKey"] = "ToFormKey is ToKey now (Links: FromKey, FromType, Field, Path, ToKey)",
        ["FromSignature"] = "FromSignature is FromType now, and holds the type's name (Weapon), not the signature",
        ["FromEditorID"] = "FromEditorID is gone: edid(FromKey) gives it",
    };

    static readonly Dictionary<string, string> Functions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fkname"] = "fkname(fk) is name(fk) now",
        ["en"] = "en(json) is gone: text columns are one language already (the query's), and text(value, 'de') gives another",
        ["tier"] = "tier() went with the old commands and has nothing in its place: read the level or the quality keyword from the record's own columns",
    };

    public static string? Table(string name) => Tables.GetValueOrDefault(name.Trim('"'));
    public static string? Column(string name) => Columns.GetValueOrDefault(name[(name.LastIndexOf('.') + 1)..].Trim('"'));
    public static string? Function(string name) => Functions.GetValueOrDefault(name.Trim('"'));
}
