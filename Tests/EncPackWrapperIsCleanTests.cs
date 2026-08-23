using Haf.Schema;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // THE SHIPPED PACK must produce NO wrapper issues. A new rule that fires on ENC's own healthy wrapper is a false
    // positive that would train every author to ignore the report — the failure mode a validator cannot survive.
    // Values read from Assets/Pack/ENCReload/pack.json, 2026-08-23.
    public class EncPackWrapperIsCleanTests
    {
        [Fact]
        public void TheShippedEncWrapperIsSilent()
        {
            var issues = PackValidator.ValidatePack(new PackValidator.PackWrapper
            {
                ModId = "enc",
                SchemaVersion = 1,
                DependsOn = new string[0],
                LoadAfter = new string[0],
                Overrides = new PackValidator.PackOverrideRef[0],
            });
            Assert.True(issues.Count == 0, "the shipped ENC wrapper trips a rule: " + string.Join(" | ", issues));
        }
    }
}
