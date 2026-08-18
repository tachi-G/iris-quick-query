using IrisQuickQuery.Core.Models;
using IrisQuickQuery.Core.Services;

namespace IrisQuickQuery.Core.Tests;

public sealed class QueryObjectTests
{
    [Fact]
    public void ExistingNineRules_MigrateToFiveSharedQueryObjectsWithoutLosingRuntimeRules()
    {
        var rules = CurrentNineRules();

        var migration = QueryObjectCompiler.MigrateRules(rules);

        Assert.Equal(9, migration.SourceRuleCount);
        Assert.Equal(5, migration.GroupedObjectCount);
        Assert.Equal(4, migration.SharedRuleReduction);
        Assert.Equal(3, migration.QueryObjects.Single(x => x.Name == "患者基本信息").Entries.Count);
        Assert.Equal(2, migration.QueryObjects.Single(x => x.Name == "就诊信息").Entries.Count);
        Assert.Equal(2, migration.QueryObjects.Single(x => x.Name == "病案号与就诊号").Entries.Count);

        var compiled = QueryObjectCompiler.Compile(migration.QueryObjects);
        Assert.Equal(9, compiled.Count);
        Assert.Equal(rules.Select(x => x.Id).Order(), compiled.Select(x => x.Id).Order());
        Assert.Contains(compiled, x => x.Name == "根据病案号取就诊号");
        var medicalRecordLookup = compiled.Single(x => x.Name == "根据病案号取就诊号");
        Assert.Contains("SELECT TOP 1", medicalRecordLookup.SqlTemplate, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY ID DESC", medicalRecordLookup.SqlTemplate, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SharedPatientObject_MapsAllPatientFieldsForEveryLookupEntry()
    {
        var migration = QueryObjectCompiler.MigrateRules(CurrentNineRules());
        var patient = migration.QueryObjects.Single(x => x.Name == "患者基本信息");

        Assert.Equal(5, patient.OutputMappings.Count);
        Assert.Contains("PAPMI_ID", patient.BaseSqlTemplate);
        Assert.Contains("PAPMI_No", patient.BaseSqlTemplate);
        Assert.Contains("PAPMI_RowId1", patient.BaseSqlTemplate);
        Assert.Equal(3, QueryObjectCompiler.Compile([patient]).Count);
        Assert.All(QueryObjectCompiler.Compile([patient]), rule => Assert.Equal(5, rule.OutputMappings.Count));
    }

    [Fact]
    public void UnsupportedComplexRule_IsKeptLosslesslyAsAdvancedEntry()
    {
        var rule = new QueryRuleDefinition
        {
            Name = "复杂查询",
            SqlTemplate = "WITH x AS (SELECT id FROM t) SELECT id FROM x WHERE id={{patient_id}}",
            OutputMappings = [new() { ColumnName = "id", ElementKey = "patient_id" }]
        };

        var migration = QueryObjectCompiler.MigrateRules([rule]);
        var queryObject = Assert.Single(migration.QueryObjects);
        var entry = Assert.Single(queryObject.Entries);

        Assert.Equal(rule.SqlTemplate, entry.SqlTemplateOverride);
        Assert.Equal(rule.SqlTemplate, Assert.Single(QueryObjectCompiler.Compile([queryObject])).SqlTemplate);
    }

    [Fact]
    public void SharedObject_RejectsWhereInBaseSqlAndWhereKeywordInEntryFilter()
    {
        var snapshot = TestConfig.CreateElements("patient_id");
        snapshot.QueryObjects.Add(new QueryObjectDefinition
        {
            Name = "患者",
            BaseSqlTemplate = "SELECT patient_id FROM patients WHERE enabled=1",
            OutputMappings = [new OutputMapping { ColumnName = "patient_id", ElementKey = "patient_id" }],
            Entries = [new QueryEntryDefinition { Name = "按编号", FilterTemplate = "WHERE patient_id={{patient_id}}" }]
        });

        var issues = ConfigurationValidator.Validate(snapshot);

        Assert.Contains(issues, x => x.Code == "object.where");
        Assert.Contains(issues, x => x.Code == "entry.where");
    }

    private static List<QueryRuleDefinition> CurrentNineRules()
    {
        static QueryRuleDefinition Rule(string name, string sql, RuleResultMode mode,
            params (string Column, string Element)[] mappings) => new()
        {
            Name = name,
            SqlTemplate = sql,
            ResultMode = mode,
            OutputMappings = mappings.Select(x => new OutputMapping { ColumnName = x.Column, ElementKey = x.Element }).ToList()
        };

        return
        [
            Rule("根据身份证取登记号", "SELECT PAPMI_No,PAPMI_DOB,PAPMI_RowId1,PAPMI_Name FROM PA_PatMas WHERE PAPMI_ID = {{id_card_no}}", RuleResultMode.Scalar,
                ("PAPMI_No", "regNo"), ("PAPMI_DOB", "birthday"), ("PAPMI_RowId1", "PAPMI_RowId1"), ("PAPMI_Name", "patName")),
            Rule("获取就诊信息", "SELECT PAADM_RowID,PAADM_AdmDate,PAADM_DischgDate,PAADM_AdmDocCodeDR,PAADM_DepCode_DR FROM PA_Adm WHERE PAADM_PAPMI_DR = {{PAPMI_RowId1}}", RuleResultMode.List,
                ("PAADM_RowID", "PAADM_RowID"), ("PAADM_AdmDate", "AdmDate"), ("PAADM_DischgDate", "DischgDate"), ("PAADM_AdmDocCodeDR", "DocCodeDR_ct"), ("PAADM_DepCode_DR", "locID")),
            Rule("医生工号和姓名", "SELECT CTPCP_Code,CTPCP_Desc FROM CT_CareProv WHERE CTPCP_RowId1 = {{DocCodeDR_ct}}", RuleResultMode.Scalar,
                ("CTPCP_Code", "docCode"), ("CTPCP_Desc", "docName")),
            Rule("获取科室", "SELECT CTLOC_Desc FROM CT_Loc WHERE CTLOC_RowID = {{locID}}", RuleResultMode.Scalar, ("CTLOC_Desc", "locDesc")),
            Rule("根据就诊号取病案号", "SELECT MrNo FROM MA_IPMR_SS.MedicareNo WHERE EpisodeID = {{PAADM_RowID}}", RuleResultMode.Scalar, ("MrNo", "MrNo")),
            Rule("根据病案号取用户ID", "SELECT TOP 1 EpisodeID FROM MA_IPMR_SS.MedicareNo WHERE MrNo= {{MrNo}} order by ID DESC", RuleResultMode.Scalar, ("EpisodeID", "PAADM_RowID")),
            Rule("根据患者ID取患者信息", "SELECT PAPMI_No,PAPMI_DOB,PAPMI_Name,PAPMI_ID FROM PA_PatMas WHERE PAPMI_RowId1 = {{PAPMI_RowId1}}", RuleResultMode.Scalar,
                ("PAPMI_No", "regNo"), ("PAPMI_DOB", "birthday"), ("PAPMI_Name", "patName"), ("PAPMI_ID", "id_card_no")),
            Rule("根据就诊号取患者ID", "SELECT PAADM_PAPMI_DR FROM PA_Adm WHERE PAADM_RowID= {{PAADM_RowID}}", RuleResultMode.Scalar, ("PAADM_PAPMI_DR", "PAPMI_RowId1")),
            Rule("根据登记号获取患者ID", "SELECT PAPMI_RowId1 FROM PA_PatMas WHERE PAPMI_No = {{regNo}}", RuleResultMode.Scalar, ("PAPMI_RowId1", "PAPMI_RowId1"))
        ];
    }
}
