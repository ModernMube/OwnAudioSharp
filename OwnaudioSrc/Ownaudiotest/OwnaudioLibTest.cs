using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ownaudio;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ownaudio.Tests
{
    [TestClass()]
    public class OwnaudioLibTest
    {
        [TestMethod()]
        public void InitializeTest()
        {
            bool result = OwnAudio.Initialize();

            Assert.IsTrue(result, "Expected Initialize to return true");
		}
	}
}
