using IsoToUsb.Services;
using IsoToUsb.ViewModels;

namespace IsoToUsb.Tests;

[TestClass]
public class MainViewModelTests
{
    [TestMethod]
    public void CanStart_False_Until_Iso_And_Drive_Selected()
    {
        var vm = new MainViewModel();
        Assert.IsFalse(vm.CanStart);

        var tempIso = Path.Combine(Path.GetTempPath(), $"sample_{Guid.NewGuid():N}.iso");
        File.WriteAllText(tempIso, "stub");
        try
        {
            vm.SetIso(tempIso);
            Assert.IsFalse(vm.CanStart, "ISO alone is not enough.");

            vm.SelectedDrive = new DiskInfo(99, "Fake USB", "SERIAL", 32UL * 1024 * 1024 * 1024, BusTypes.Usb, false, false, false);
            Assert.IsTrue(vm.CanStart, "ISO + drive should enable Start.");

            vm.IsBusy = true;
            Assert.IsFalse(vm.CanStart, "Busy should disable Start.");
        }
        finally
        {
            File.Delete(tempIso);
        }
    }

    [TestMethod]
    public void SetIso_Ignores_Non_Iso_Extensions()
    {
        var vm = new MainViewModel();
        var tempTxt = Path.Combine(Path.GetTempPath(), $"sample_{Guid.NewGuid():N}.txt");
        File.WriteAllText(tempTxt, "x");
        try
        {
            vm.SetIso(tempTxt);
            Assert.IsNull(vm.IsoPath);
        }
        finally
        {
            File.Delete(tempTxt);
        }
    }

    [TestMethod]
    public void SetIso_Ignores_Missing_File()
    {
        var vm = new MainViewModel();
        vm.SetIso(@"C:\definitely-not-there-" + Guid.NewGuid().ToString("N") + ".iso");
        Assert.IsNull(vm.IsoPath);
    }
}
