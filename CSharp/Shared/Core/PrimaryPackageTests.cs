using System;
using System.IO;
using System.Xml.Linq;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;
using Voronoi2;
using NetModule = global::INet;
using PrimaryPackage.Fault;
using PrimaryPackage.Core.Fault;

namespace PrimaryPackage.Core {
    public static class PrimaryPackageTests {
        private struct TestState {
            public int X;
            public float Y;
            public uint Z;
        }

        public static bool RunAllTests(out string summary) {
            int passed = 0;
            int failed = 0;
            var log = new System.Text.StringBuilder();

            void Assert(string name, bool condition) {
                if (condition) {
                    passed++;
                    log.Append("[PASS] ").Append(name).Append('\n');
                } else {
                    failed++;
                    log.Append("[FAIL] ").Append(name).Append('\n');
                }
            }

            try {
                Assert("ColdNet.Quantize.Bounds", Cold.ColdNet.Quantize(-50.0f) == 0 && Cold.ColdNet.Quantize(100.0f) == 255);

                float[] probeTemps = { -45.0f, -20.0f, 0.0f, 15.0f, 20.0f, 35.0f, 60.0f };
                bool quantPrecision = true;
                for (int i = 0; i < probeTemps.Length; i++) {
                    byte q = Cold.ColdNet.Quantize(probeTemps[i]);
                    float restored = Cold.ColdNet.Dequantize(q);
                    if (Math.Abs(restored - probeTemps[i]) > 0.5f) {
                        quantPrecision = false;
                        break;
                    }
                }
                Assert("ColdNet.Quantize.Precision", quantPrecision);

                var field = new BitField(4, 8);
                uint val = field.Write(0, 123);
                Assert("BitField.WriteRead", field.Read(val) == 123);

                var testLedger = new FaultLedger(static (_, _) => { });
                var site = testLedger.Register("test_site", FaultPolicy.Tick, false);
                Assert("FaultLedger.SiteRegistration", site != null && site.Id == "test_site");

                bool breakerTripped = false;
                for (int i = 0; i < 10; i++) {
                    var outcome = testLedger.Record(site, null, null, new InvalidOperationException("Test exception"), i);
                    if (outcome.Tripped) {
                        breakerTripped = true;
                        break;
                    }
                }
                Assert("FaultLedger.CircuitBreakerTrip", breakerTripped);

                string modDir = ModAssets.Root;
                if (!string.IsNullOrEmpty(modDir)) {
                    string filelistPath = Path.Combine(modDir, "filelist.xml");
                    Assert("Manifest.FileListExists", File.Exists(filelistPath));

                    if (File.Exists(filelistPath)) {
                        XDocument doc = XDocument.Load(filelistPath);
                        bool allFilesExist = true;
                        foreach (XElement el in doc.Root.Elements()) {
                            string fileAttr = (string)el.Attribute("file");
                            if (string.IsNullOrEmpty(fileAttr)) { continue; }
                            string resolved = fileAttr.Replace("%ModDir%", modDir).Replace('/', Path.DirectorySeparatorChar);
                            if (!File.Exists(resolved)) {
                                allFilesExist = false;
                                log.Append("Missing file: ").Append(resolved).Append('\n');
                            }
                        }
                        Assert("Manifest.FilesReferencedExist", allFilesExist);
                    }

                    string modConfigPath = Path.Combine(modDir, "ModConfig.xml");
                    Assert("Manifest.ModConfigExists", File.Exists(modConfigPath));
                }

                Assert("INet.ChannelInit", NetModule.Net.Channel(Cold.ColdNet.StateId) != null);
                Assert("INet.ReportFormat", !string.IsNullOrEmpty(NetModule.Net.Report()));

                byte[] crcData = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
                ushort crc1 = NetModule.NativeWire.ComputeCrc16(crcData);
                ushort crc2 = NetModule.NativeWire.ComputeCrc16(crcData);
                Assert("NativeWire.Crc16Deterministic", crc1 != 0 && crc1 == crc2);

                byte[] deltaCurrent = new byte[] { 10, 20, 35, 40, 50 };
                byte[] deltaBaseline = new byte[] { 10, 20, 30, 40, 50 };
                var writeMsg = new WriteOnlyMessage();
                int changed = NetModule.NetDelta.WriteByteDelta(writeMsg, deltaCurrent, deltaBaseline);
                Assert("NetDelta.ChangedCount", changed == 1);

                var readMsg = new ReadWriteMessage(writeMsg.Buffer, 0, writeMsg.LengthBits, false);
                byte[] deltaTarget = new byte[5];
                Array.Copy(deltaBaseline, deltaTarget, 5);
                int updated = NetModule.NetDelta.ReadByteDelta(readMsg, deltaTarget);
                Assert("NetDelta.AppliedCount", updated == 1 && deltaTarget[2] == 35);

                using (var rented = NetModule.NetMemoryPool.Rent(256)) {
                    Assert("NetMemoryPool.Rent", rented.Capacity >= 256 && rented.Span.Length >= 256);
                }

                byte[] rawNibbles = { 3, 12, 7, 15 };
                byte[] packedNibbles = new byte[2];
                NetModule.NetCompression.PackNibbles(rawNibbles, packedNibbles);
                byte[] unpackedNibbles = new byte[4];
                NetModule.NetCompression.UnpackNibbles(packedNibbles, unpackedNibbles, 4);
                Assert("NetCompression.Nibbles", unpackedNibbles[0] == 3 && unpackedNibbles[1] == 12 && unpackedNibbles[2] == 7 && unpackedNibbles[3] == 15);

                byte[] rleSource = { 5, 5, 5, 5, 5, 9, 9, 9, 9, 1 };
                byte[] rleCompressed = new byte[32];
                int compLen = NetModule.NetCompression.CompressRle(rleSource, rleCompressed);
                byte[] rleDecompressed = new byte[10];
                int decompLen = NetModule.NetCompression.DecompressRle(rleCompressed.AsSpan(0, compLen), rleDecompressed);
                Assert("NetCompression.RLE", decompLen == 10 && rleDecompressed[0] == 5 && rleDecompressed[5] == 9 && rleDecompressed[9] == 1);

                var snapHistory = new NetModule.SnapshotHistory<TestState>();
                snapHistory.Record(10, 1.0f, new TestState { X = 100, Y = 2.5f, Z = 50 });
                snapHistory.Record(11, 1.1f, new TestState { X = 100, Y = 7.5f, Z = 50 });
                byte[] snapDelta = new byte[64];
                int deltaBytes = snapHistory.ComputeDelta(10, 11, snapDelta);
                Assert("SnapshotHistory.DeltaCompute", deltaBytes > 0);

                snapHistory.TryGet(10, out TestState baseSnap, out _);
                bool appliedOk = NetModule.SnapshotHistory<TestState>.ApplyDelta(baseSnap, snapDelta.AsSpan(0, deltaBytes), out TestState targetSnap);
                Assert("SnapshotHistory.DeltaApply", appliedOk && targetSnap.X == 100 && Math.Abs(targetSnap.Y - 7.5f) < 0.001f && targetSnap.Z == 50);

                var interpolator = new NetModule.NetInterpolator();
                interpolator.AddSample(0.0f, 10.0f);
                interpolator.AddSample(1.0f, 20.0f);
                float mid = interpolator.Evaluate(0.5f);
                Assert("NetInterpolator.Evaluate", Math.Abs(mid - 15.0f) < 0.001f);

                var seq = new NetModule.NetSequencer();
                var seqMsg = new WriteOnlyMessage();
                seq.WriteHeader(seqMsg, 1.0f);
                var seqRead = new ReadWriteMessage(seqMsg.Buffer, 0, seqMsg.LengthBits, false);
                bool readOk = seq.ReadHeader(seqRead, 1.05f, out ushort inSeq, out _);
                Assert("NetSequencer.RoundTrip", readOk && inSeq == 1);

                // Reject before touching the live queue: verification must not
                // send test data to players or flush their pending packets.
                Assert("NetMultiplexer.RejectOversized", !NetModule.NetMultiplexer.Enqueue(100, new byte[1100]));

                NetModule.NetPing.PingPacket pingPkt = new NetModule.NetPing.PingPacket {
                    Sequence = 42,
                    ClientTimeUs = 1_000_000L,
                    ServerTimeUs = 1_000_500L
                };
                Assert("NetPing.PacketStructure", pingPkt.Sequence == 42 && pingPkt.ServerTimeUs > pingPkt.ClientTimeUs);

                var vCell = new VoronoiCell(new Vector2[] { new Vector2(50, 50) });
                vCell.Edges.Add(new GraphEdge(new Vector2(0, 0), new Vector2(100, 0)));
                vCell.Edges.Add(new GraphEdge(new Vector2(100, 0), new Vector2(100, 100)));
                vCell.Edges.Add(new GraphEdge(new Vector2(100, 100), new Vector2(0, 100)));
                vCell.Edges.Add(new GraphEdge(new Vector2(0, 100), new Vector2(0, 0)));

                bool insideRejected = NetModule.NetSyncAcceleratorPlugin.FastAabbReject(vCell, new Vector2(50, 50));
                bool outsideRejected = NetModule.NetSyncAcceleratorPlugin.FastAabbReject(vCell, new Vector2(150, 50));
                bool outsideRejectedY = NetModule.NetSyncAcceleratorPlugin.FastAabbReject(vCell, new Vector2(50, -10));
                Assert("VoronoiCell.FastAabbReject", !insideRejected && outsideRejected && outsideRejectedY);

            } catch (Exception ex) {
                failed++;
                log.Append("[EXCEPTION] ").Append(ex.Message).Append('\n').Append(ex.StackTrace).Append('\n');
            }

            summary = $"Passed: {passed}, Failed: {failed}\n" + log.ToString();
            return failed == 0;
        }
    }
}
