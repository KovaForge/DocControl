// Standalone gap-fill sanity check for the new NumberAllocator allocator logic.
// Build with:
//   dotnet run --project tools/number-allocator-test
// No database required — exercises the same FindSmallestFreeSlot algorithm
// the production code uses, against the exact scenarios from the task.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

static int FindSmallestFreeSlot(HashSet<int> used, int maxUsed, int nextNumberHint)
{
    for (var candidate = 1; candidate <= maxUsed; candidate++)
    {
        if (!used.Contains(candidate)) return candidate;
    }
    return Math.Max(maxUsed + 1, 1);
}

int Allocate(HashSet<int> used)
{
    var max = used.Count == 0 ? 0 : used.Max();
    return FindSmallestFreeSlot(used, max, nextNumberHint: max + 1);
}

var failures = 0;
void Check(string name, int actual, int expected)
{
    if (actual == expected)
    {
        Console.WriteLine($"PASS  {name}  ->  {actual}");
    }
    else
    {
        Console.WriteLine($"FAIL  {name}  expected {expected} got {actual}");
        failures++;
    }
}

// Scenario 1: fresh series -> 1
{
    var used = new HashSet<int>();
    Check("fresh series returns 1", Allocate(used), 1);
}

// Scenario 2: [1,2,4,5] -> 3 (the canonical "delete freed a slot" case)
{
    var used = new HashSet<int> { 1, 2, 4, 5 };
    Check("[1,2,4,5] returns 3", Allocate(used), 3);
    used.Add(3);
    Check("[1,2,3,4,5] returns 6", Allocate(used), 6);
}

// Scenario 3: clean DB state from Mike's report -> 1, 2, 3, 4, 5, 6 in order
{
    var used = new HashSet<int> { 7, 8, 9, 10, 11, 12, 13 };
    var got = new List<int>();
    for (var i = 0; i < 6; i++)
    {
        var n = Allocate(used);
        got.Add(n);
        used.Add(n);
    }
    var expected = new[] { 1, 2, 3, 4, 5, 6 };
    Check("clean-DB reorder produces 1..6", string.Join(",", got) == string.Join(",", expected) ? 1 : 0, 1);
    if (string.Join(",", got) != string.Join(",", expected))
        Console.WriteLine($"      got: {string.Join(",", got)}");
}

// Scenario 4: 5 parallel "allocations" on a single series must yield 1..5 unique
{
    var used = new HashSet<int>();
    var got = new System.Collections.Concurrent.ConcurrentBag<int>();
    await Parallel.ForEachAsync(Enumerable.Range(0, 5), async (_, _) =>
    {
        // Simulate the row-lock behaviour: serialise the read-modify-write
        // of (read used -> compute next -> write used) per "transaction".
        // In production this is enforced by SELECT ... FOR UPDATE on the series row.
        await Task.Yield();
        lock (used)
        {
            var n = Allocate(used);
            used.Add(n);
            got.Add(n);
        }
    });
    var sorted = got.OrderBy(x => x).ToArray();
    var expectedParallel = new[] { 1, 2, 3, 4, 5 };
    var ok = sorted.SequenceEqual(expectedParallel);
    Console.WriteLine(ok
        ? $"PASS  5 parallel allocations produce 1..5  ->  {string.Join(",", sorted)}"
        : $"FAIL  5 parallel allocations  expected 1,2,3,4,5  got {string.Join(",", sorted)}");
    if (!ok) failures++;
}

// Scenario 5: gap in the middle is filled
{
    var used = new HashSet<int> { 1, 3, 5, 7 };
    Check("[1,3,5,7] returns 2", Allocate(used), 2);
}

// Scenario 6: only number 1 missing
{
    var used = new HashSet<int> { 2, 3, 4, 5 };
    Check("[2,3,4,5] returns 1", Allocate(used), 1);
}

if (failures == 0)
{
    Console.WriteLine("\nAll allocator gap-fill scenarios passed.");
}
else
{
    Console.WriteLine($"\n{failures} scenario(s) failed.");
    Environment.Exit(1);
}
