using System.Runtime.InteropServices;

using NUnit.Framework;

using Picollo.PerfEvent;

using Shouldly;

namespace Picollo.Tests.PerfEvent;

[TestFixture]
public class NativeMethodTests
{
    [SetUp]
    public void Setup()
    {
    }

    [Test]
    public void TestIsSupported()
    {
        bool expected = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && 
                         RuntimeInformation.OSArchitecture == Architecture.X64;
        
        NativeMethods.IsSupported().ShouldBe(expected);
    }
}
