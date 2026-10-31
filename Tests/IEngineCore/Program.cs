using System;
using System.Collections.Generic;
using IEngine.Simulation;

static class Program
{
    static int passed;
    static void Check(bool ok, string message)
    {
        if (!ok) { throw new InvalidOperationException(message); }
    }

    static void Test(string label, Action action)
    {
        action();
        passed++;
        Console.WriteLine("PASS " + label);
    }

    static void Main()
    {
        Test("duplicate wakes bounded", () =>
        {
            var q = new IndexedDeadlineQueue();
            for (int i = 0; i < 100000; i++)
            {
                q.Set(11, i + 5.0);
                Check(q.Count == 1, "duplicate scheduled slot");
            }
            Check(!q.TryTakeExpired(100000.0, out _), "extended deadline expired early");
            Check(q.TryTakeExpired(100004.0, out int slot) && slot == 11, "wrong extension");
            Check(q.Count == 0 && !q.Contains(11), "expired slot still tracked");
        });

        Test("stable order and both key directions", () =>
        {
            var q = new IndexedDeadlineQueue();
            q.Set(7, 8);
            q.Set(3, 5);
            q.Set(5, 7);
            q.Set(7, 2);
            q.Set(3, 11);
            Check(q.TryTakeExpired(2, out int a) && a == 7, "decreased deadline not first");
            Check(!q.TryTakeExpired(6, out _), "deadline increase not applied");
            Check(q.TryTakeExpired(7, out int b) && b == 5, "wrong order");
            Check(q.TryTakeExpired(11, out int c) && c == 3, "missing late deadline");
            Check(!q.TryTakeExpired(12, out _), "duplicate expired slot");
        });

        Test("removal, clear and slot reuse", () =>
        {
            var q = new IndexedDeadlineQueue();
            q.Set(0, 10);
            q.Set(1, 5);
            q.Set(25000, 3);
            Check(q.Remove(1), "remove failed");
            Check(!q.Remove(1), "double removal succeeded");
            Check(q.TryTakeExpired(3, out int slot) && slot == 25000, "high slot not selected");
            Check(!q.Contains(25000), "high slot still present");
            q.Clear();
            Check(q.Count == 0 && !q.Contains(0), "clear left active node");
            q.Set(0, 4);
            Check(q.TryTakeExpired(4, out slot) && slot == 0, "slot not reusable");
        });

        Test("randomized oracle", () =>
        {
            var q = new IndexedDeadlineQueue();
            var reference = new Dictionary<int, double>();
            var random = new Random(1729);
            for (int iteration = 0; iteration < 50000; iteration++)
            {
                int slot = random.Next(0, 500);
                int operation = random.Next(0, 6);
                if (operation <= 2)
                {
                    double deadline = random.Next(0, 6000) + random.NextDouble();
                    q.Set(slot, deadline);
                    reference[slot] = deadline;
                }
                else if (operation == 3)
                {
                    Check(q.Remove(slot) == reference.Remove(slot), "remove mismatch");
                }
                else if (operation == 4)
                {
                    double time = random.Next(0, 6000);
                    if (reference.Count > 0)
                    {
                        int expectedSlot = -1;
                        double expectedTime = double.PositiveInfinity;
                        foreach (var pair in reference)
                        {
                            if (pair.Value < expectedTime || (pair.Value == expectedTime && pair.Key < expectedSlot))
                            {
                                expectedSlot = pair.Key;
                                expectedTime = pair.Value;
                            }
                        }
                        bool due = expectedTime <= time;
                        Check(q.TryTakeExpired(time, out int actualSlot) == due, "expired state mismatch");
                        if (due)
                        {
                            Check(actualSlot == expectedSlot, "heap order mismatch");
                            reference.Remove(expectedSlot);
                        }
                    }
                    else { Check(!q.TryTakeExpired(time, out _), "empty queue expired"); }
                }
                else
                {
                    q.Clear();
                    reference.Clear();
                }
                Check(q.Count == reference.Count, "heap/reference size mismatch");
            }
        });
        Console.WriteLine($"PASS={passed} FAIL=0");
    }
}
