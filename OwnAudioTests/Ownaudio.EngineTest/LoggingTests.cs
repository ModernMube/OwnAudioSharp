using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Logger;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ownaudio.EngineTest
{
    /// <summary>
    /// Covers the sink dispatch, the level filter and the rotating file writer's retention.
    /// </summary>
    [TestClass]
    public class LoggingTests
    {
        private Log.Level _savedLevel;
        private string _directory = string.Empty;

        [TestInitialize]
        public void Setup()
        {
            _savedLevel = Log.LoggerLevel;
            _directory = Path.Combine(Path.GetTempPath(), "ownaudio_logtest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        [TestCleanup]
        public void Cleanup()
        {
            Log.CloseFile();
            Log.LoggerLevel = _savedLevel;

            try { Directory.Delete(_directory, recursive: true); }
            catch { }
        }

        private string _logPath => Path.Combine(_directory, "ownaudio.log");

        [TestMethod]
        public void Sink_ReceivesEveryLineThatPassesTheLevel()
        {
            var _received = new List<(Log.Level Level, string Message)>();
            void _handler(Log.Level level, string message) => _received.Add((level, message));

            Log.LoggerLevel = Log.Level.Info;
            Log.Sink += _handler;

            try
            {
                Log.Info("first");
                Log.Warning("second");
            }
            finally
            {
                Log.Sink -= _handler;
            }

            Assert.AreEqual(2, _received.Count, "Both lines should have reached the sink");
            Assert.AreEqual(Log.Level.Info, _received[0].Level);
            StringAssert.Contains(_received[0].Message, "first");
            Assert.AreEqual(Log.Level.Warning, _received[1].Level);
        }

        [TestMethod]
        public void Sink_StaysSilentBelowTheConfiguredLevel()
        {
            var _received = new List<string>();
            void _handler(Log.Level level, string message) => _received.Add(message);

            Log.LoggerLevel = Log.Level.Error;
            Log.Sink += _handler;

            try
            {
                Log.Info("filtered out");
                Log.Warning("filtered out too");
                Log.Error("kept");
            }
            finally
            {
                Log.Sink -= _handler;
            }

            Assert.AreEqual(1, _received.Count, "Only the error passes an Error level filter");
            StringAssert.Contains(_received[0], "kept");
        }

        [TestMethod]
        public void ThrowingSink_DoesNotEscapeToTheCaller()
        {
            void _handler(Log.Level level, string message) => throw new InvalidOperationException("host logger blew up");

            Log.LoggerLevel = Log.Level.Info;
            Log.Sink += _handler;

            try { Log.Info("still fine"); }
            finally { Log.Sink -= _handler; }
        }

        [TestMethod]
        public void DisabledLevel_WritesNothingAtAllEvenWithAnException()
        {
            TextWriter _saved = Console.Out;
            var _console = new StringWriter();
            Console.SetOut(_console);

            try
            {
                Log.LoggerLevel = Log.Level.Disabled;
                Log.Error("[Mixer] something went wrong", new InvalidOperationException("boom"));
                Log.FatalError("[Mixer] something died", new InvalidOperationException("bang"));
            }
            finally
            {
                Console.SetOut(_saved);
            }

            Assert.AreEqual(string.Empty, _console.ToString(),
                "A disabled logger must not print the exception either");
        }

        [TestMethod]
        public void Error_CarriesTheExceptionChainAndItsStack()
        {
            string? _line = null;
            void _handler(Log.Level level, string message) => _line = message;

            Log.LoggerLevel = Log.Level.Error;
            Log.Sink += _handler;

            try
            {
                try
                {
                    try { throw new InvalidOperationException("inner cause"); }
                    catch (Exception inner) { throw new AudioTestException("wrapper", inner); }
                }
                catch (Exception ex) { Log.Error("[Test] outer", ex); }
            }
            finally
            {
                Log.Sink -= _handler;
            }

            Assert.IsNotNull(_line);
            StringAssert.Contains(_line, "[Test] outer");
            StringAssert.Contains(_line, "AudioTestException: wrapper");
            StringAssert.Contains(_line, "InvalidOperationException: inner cause");
            StringAssert.Contains(_line, "LoggingTests");
        }

        [TestMethod]
        public void ToFile_WritesTheLinesAndReportsThePath()
        {
            Log.LoggerLevel = Log.Level.Info;
            string? _path = Log.ToFile(_logPath);

            Assert.AreEqual(_logPath, _path);
            Assert.AreEqual(_logPath, Log.FilePath);

            Log.Info("[Test] on disk");
            Log.CloseFile();

            StringAssert.Contains(File.ReadAllText(_logPath), "[Test] on disk");
            Assert.IsNull(Log.FilePath, "CloseFile should drop the path");
        }

        [TestMethod]
        public void ToFile_RotatesAtTheCapAndKeepsOnlyTheAllowedGenerations()
        {
            Log.LoggerLevel = Log.Level.Info;
            Log.ToFile(_logPath, maxFileSizeKb: 64, keepFiles: 2);

            string _filler = new string('x', 400);
            for (int i = 0; i < 1200; i++)
                Log.Info(_filler);

            Log.CloseFile();

            Assert.IsTrue(File.Exists(_logPath), "The live file stays");
            Assert.IsTrue(File.Exists(_rotated(1)), "The first rotation stays");
            Assert.IsTrue(File.Exists(_rotated(2)), "The second rotation stays");
            Assert.IsFalse(File.Exists(_rotated(3)), "Anything past the keep count is dropped");

            long _total = 0;
            foreach (string file in Directory.GetFiles(_directory))
                _total += new FileInfo(file).Length;

            Assert.IsTrue(_total <= 3 * 64 * 1024 + 4096,
                $"Disk use must stay near the cap, was {_total} bytes");
        }

        [TestMethod]
        public void ToFile_PrunesRotatedFilesPastTheKeepCountAtOpen()
        {
            for (int i = 1; i <= 5; i++)
                File.WriteAllText(_rotated(i), "old");

            Log.LoggerLevel = Log.Level.Info;
            Log.ToFile(_logPath, keepFiles: 2);
            Log.CloseFile();

            Assert.IsTrue(File.Exists(_rotated(1)));
            Assert.IsTrue(File.Exists(_rotated(2)));
            Assert.IsFalse(File.Exists(_rotated(3)), "A generation past the keep count is deleted at open");
            Assert.IsFalse(File.Exists(_rotated(4)));
            Assert.IsFalse(File.Exists(_rotated(5)));
        }

        [TestMethod]
        public void ToFile_PrunesRotatedFilesOlderThanMaxAge()
        {
            File.WriteAllText(_rotated(1), "stale");
            File.SetLastWriteTimeUtc(_rotated(1), DateTime.UtcNow.AddDays(-40));
            File.WriteAllText(_rotated(2), "fresh");

            Log.LoggerLevel = Log.Level.Info;
            Log.ToFile(_logPath, keepFiles: 3, maxAgeDays: 30);
            Log.CloseFile();

            Assert.IsFalse(File.Exists(_rotated(1)), "A rotated file past its age is deleted");
            Assert.IsTrue(File.Exists(_rotated(2)), "A recent one is kept");
        }

        [TestMethod]
        public void ToFile_OnAnUnusablePathFallsBackToTheConsoleInsteadOfThrowing()
        {
            string _blocker = Path.Combine(_directory, "blocker");
            File.WriteAllText(_blocker, "a file where a directory would have to be");

            TextWriter _saved = Console.Out;
            var _console = new StringWriter();
            Console.SetOut(_console);

            try
            {
                Log.LoggerLevel = Log.Level.Info;
                string? _path = Log.ToFile(Path.Combine(_blocker, "ownaudio.log"));

                Assert.IsNull(_path, "An unopenable file reports null");
                Assert.IsNull(Log.FilePath);

                Log.Info("[Test] console fallback");
            }
            finally
            {
                Console.SetOut(_saved);
            }

            StringAssert.Contains(_console.ToString(), "File logging could not start");
            StringAssert.Contains(_console.ToString(), "[Test] console fallback");
        }

        private string _rotated(int index) => Path.Combine(_directory, $"ownaudio.{index}.log");

        private sealed class AudioTestException : Exception
        {
            public AudioTestException(string message, Exception inner) : base(message, inner) { }
        }
    }
}
