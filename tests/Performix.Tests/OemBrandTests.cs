// Vendor detection tests (settings IA v2, Services page).
//
// The brand shown by the Services page is decided by OemBrandCatalog.Detect() from the two
// manufacturer strings, and the service list is filtered by OemBrandCatalog.IsBrandService().
// Both are pure functions, so the mapping is testable without a machine of that brand:
//
//   • every official spelling ("ASUS", not "Asus"; "GIGABYTE", not "Gigabyte"),
//   • the manufacturer strings real BIOSes report (ASUSTeK, Micro-Star, Hewlett-Packard, …),
//   • brand placeholders ("To Be Filled By O.E.M.") read as Unknown, never as a brand,
//   • an ASUS machine does not match Acer services and vice versa,
//   • the protected services (PSSvc above all) never match any brand.

using Predex.WinUI.Services;

namespace PreySense.Tests;

[TestClass]
public class OemBrandTests
{
    [TestMethod]
    [DataRow("ASUSTeK COMPUTER INC.", OemBrand.Asus)]
    [DataRow("ASUS", OemBrand.Asus)]
    [DataRow("asustek computer inc.", OemBrand.Asus)]
    [DataRow("Acer", OemBrand.Acer)]
    [DataRow("Acer Incorporated", OemBrand.Acer)]
    [DataRow("LENOVO", OemBrand.Lenovo)]
    [DataRow("Hewlett-Packard", OemBrand.Hp)]
    [DataRow("HP", OemBrand.Hp)]
    [DataRow("Dell Inc.", OemBrand.Dell)]
    [DataRow("Alienware", OemBrand.Alienware)]
    [DataRow("Micro-Star International Co., Ltd.", OemBrand.Msi)]
    [DataRow("MSI", OemBrand.Msi)]
    [DataRow("Razer", OemBrand.Razer)]
    [DataRow("GIGA-BYTE TECHNOLOGY CO., LTD.", OemBrand.Gigabyte)]
    [DataRow("Samsung Electronics", OemBrand.Samsung)]
    [DataRow("Microsoft Corporation", OemBrand.Microsoft)]
    [DataRow("HUAWEI", OemBrand.Huawei)]
    [DataRow("TOSHIBA", OemBrand.Toshiba)]
    [DataRow("FUJITSU", OemBrand.Fujitsu)]
    public void ManufacturerStrings_MapToTheVendor(string manufacturer, OemBrand expected)
        => Assert.AreEqual(expected, OemBrandCatalog.DetectSingle(manufacturer));

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("To Be Filled By O.E.M.")]
    [DataRow("System manufacturer")]
    [DataRow("Default string")]
    [DataRow("Some Unknown Builder")]
    public void PlaceholderAndUnknownStrings_StayUnidentified(string? manufacturer)
    {
        Assert.AreEqual(OemBrand.Unknown, OemBrandCatalog.DetectSingle(manufacturer));
        // And the neutral path is what the page renders: no invented name.
        Assert.IsNull(OemBrandCatalog.DisplayName(OemBrand.Unknown));
    }

    [TestMethod]
    public void AlienwareBoard_IsNotReportedAsDell()
    {
        // An Alienware board reports "Dell Inc." as the baseboard maker; the system field wins.
        Assert.AreEqual(OemBrand.Alienware, OemBrandCatalog.Detect("Alienware", "Dell Inc."));
        // Without an Alienware system field, the Dell board is the Dell answer.
        Assert.AreEqual(OemBrand.Dell, OemBrandCatalog.Detect("Dell Inc.", "Dell Inc."));
    }

    [TestMethod]
    public void OfficialSpellings_AreExact()
    {
        Assert.AreEqual("ASUS", OemBrandCatalog.DisplayName(OemBrand.Asus));
        Assert.AreEqual("Acer", OemBrandCatalog.DisplayName(OemBrand.Acer));
        Assert.AreEqual("Lenovo", OemBrandCatalog.DisplayName(OemBrand.Lenovo));
        Assert.AreEqual("HP", OemBrandCatalog.DisplayName(OemBrand.Hp));
        Assert.AreEqual("Dell", OemBrandCatalog.DisplayName(OemBrand.Dell));
        Assert.AreEqual("Alienware", OemBrandCatalog.DisplayName(OemBrand.Alienware));
        Assert.AreEqual("MSI", OemBrandCatalog.DisplayName(OemBrand.Msi));
        Assert.AreEqual("Razer", OemBrandCatalog.DisplayName(OemBrand.Razer));
        Assert.AreEqual("GIGABYTE", OemBrandCatalog.DisplayName(OemBrand.Gigabyte));
    }

    [TestMethod]
    public void AsusMachine_MatchesAsusServices_NotAcerOnes()
    {
        Assert.IsTrue(OemBrandCatalog.IsBrandService(OemBrand.Asus,
            "ASUSOptimization", "ASUS Optimization Service"));
        Assert.IsTrue(OemBrandCatalog.IsBrandService(OemBrand.Asus,
            "ArmouryCrateService", "Armoury Crate Service"));
        Assert.IsFalse(OemBrandCatalog.IsBrandService(OemBrand.Asus,
            "AcerServiceSvc", "Acer Service"));
        Assert.IsFalse(OemBrandCatalog.IsBrandService(OemBrand.Asus,
            "PredatorSenseService", "Predator Sense Service"));
    }

    [TestMethod]
    public void AcerMachine_MatchesAcerServices_NotAsusOnes()
    {
        Assert.IsTrue(OemBrandCatalog.IsBrandService(OemBrand.Acer,
            "AcerServiceSvc", "Acer Service"));
        Assert.IsTrue(OemBrandCatalog.IsBrandService(OemBrand.Acer,
            "PredatorSenseService", "Predator Sense Service"));
        Assert.IsFalse(OemBrandCatalog.IsBrandService(OemBrand.Acer,
            "ASUSOptimization", "ASUS Optimization Service"));
        Assert.IsFalse(OemBrandCatalog.IsBrandService(OemBrand.Acer,
            "ArmouryCrateService", "Armoury Crate Service"));
    }

    [TestMethod]
    public void ProtectedServices_NeverMatchAnyBrand()
    {
        foreach (OemBrand brand in Enum.GetValues<OemBrand>())
        {
            foreach (string protectedName in OemBrandCatalog.ProtectedServices)
            {
                Assert.IsFalse(OemBrandCatalog.IsBrandService(brand, protectedName, protectedName),
                    $"{protectedName} must never be offered for stopping ({brand})");
            }
        }

        // PSSvc's display name is "Predator Service", which WOULD match the Acer token set; the
        // name check is what keeps it out of the list.
        Assert.IsFalse(OemBrandCatalog.IsBrandService(OemBrand.Acer, "PSSvc", "Predator Service"));
    }

    [TestMethod]
    public void UnknownBrand_MatchesNothing()
    {
        Assert.IsFalse(OemBrandCatalog.IsBrandService(OemBrand.Unknown, "AcerServiceSvc", "Acer Service"));
        Assert.IsFalse(OemBrandCatalog.IsBrandService(OemBrand.Unknown, "ASUSOptimization", "ASUS Optimization"));
        Assert.AreEqual(0, OemBrandCatalog.ServiceTokens(OemBrand.Unknown).Count);
    }

    [TestMethod]
    public void Filter_KeepsOnlyTheBrandsServices()
    {
        var installed = new List<OemServiceRef>
        {
            new("AcerServiceSvc", "Acer Service"),
            new("ASUSOptimization", "ASUS Optimization Service"),
            new("PSSvc", "Predator Service"),
            new("Spooler", "Print Spooler"),
        };

        List<OemServiceRef> asus = OemBrandCatalog.Filter(OemBrand.Asus, installed);
        Assert.AreEqual(1, asus.Count);
        Assert.AreEqual("ASUSOptimization", asus[0].Name);

        List<OemServiceRef> acer = OemBrandCatalog.Filter(OemBrand.Acer, installed);
        Assert.AreEqual(1, acer.Count);
        Assert.AreEqual("AcerServiceSvc", acer[0].Name);
    }

    [TestMethod]
    public void ControlAppNames_AreTheVendorsOwnProducts()
    {
        Assert.AreEqual("Predator Sense", OemBrandCatalog.ControlAppName(OemBrand.Acer));
        Assert.AreEqual("Armoury Crate", OemBrandCatalog.ControlAppName(OemBrand.Asus));
        Assert.AreEqual("Lenovo Vantage", OemBrandCatalog.ControlAppName(OemBrand.Lenovo));
        Assert.IsNull(OemBrandCatalog.ControlAppName(OemBrand.Unknown));
    }
}
