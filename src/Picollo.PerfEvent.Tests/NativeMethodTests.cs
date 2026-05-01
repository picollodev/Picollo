using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Shouldly;

namespace Picollo.PerfEvent.Tests;

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
