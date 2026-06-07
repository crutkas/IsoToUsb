namespace IsoToUsb.Tests;

[TestClass]
public sealed class ScaffoldTests
{
    [TestMethod]
    public void ProjectReference_To_IsoToUsb_Resolves()
    {
        // Smoke test: confirms the test project successfully references the main
        // IsoToUsb assembly. Specific functionality is exercised by per-feature
        // test classes added alongside each Services\* implementation.
        var asm = typeof(App).Assembly;
        Assert.IsNotNull(asm);
        Assert.AreEqual("IsoToUsb", asm.GetName().Name);
    }
}
