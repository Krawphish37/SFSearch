-- Quality keyword check: new armor/weapons whose templates include a quality OMOD
-- (directly, or as a tier of an included TiersAny) whose target keyword the item doesn't carry.
-- Such an OMOD never attaches in game.
-- Run: Query <plugin> --file "<this file>"
-- Works when the quality OMODs are in the plugin itself. For a patch that reuses its master's
-- OMODs (e.g. darkstar.esm's), run with --with-masters and join sf. / darkstar. instead of Records.

CREATE TEMP TABLE inc AS SELECT r.FormKey, r.EditorID, t.key AS tpl, i.value ->> 'Mod' AS m
  FROM Records r, json_each(r.Data,'$.ObjectTemplates') t, json_each(t.value,'$.Includes') i WHERE r.Signature IN ('Armor','Weapon') AND r.IsOverride = 0;
CREATE TEMP TABLE qm AS
  SELECT inc.FormKey, inc.EditorID, inc.tpl, inc.m AS via, inc.m AS q FROM inc JOIN Records o ON o.FormKey = inc.m
   WHERE o.EditorID LIKE '%Quality%' AND json_type(o.Data,'$.Includes') IS NULL
  UNION ALL SELECT inc.FormKey, inc.EditorID, inc.tpl, inc.m, j.value ->> 'Mod' FROM inc JOIN Records ta ON ta.FormKey = inc.m, json_each(ta.Data,'$.Includes') j
   WHERE ta.EditorID LIKE '%Quality%';
-- ## Quality OMODs whose target keyword the item lacks
SELECT qm.EditorID item, qm.tpl, edid(qm.via) via, COUNT(*) n, MIN(edid(qm.q)) example_omod, edid(k.value) target_kw
FROM qm JOIN Records o ON o.FormKey = qm.q, json_each(o.Data,'$.TargetOmodKeywords') k
WHERE NOT EXISTS (SELECT 1 FROM Records a, json_each(a.Data,'$.Keywords') ak WHERE a.FormKey = qm.FormKey AND ak.value = k.value)
GROUP BY 1,2,3,6 ORDER BY 1;
