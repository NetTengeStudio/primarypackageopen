using System;
using System.Collections.Generic;
using Barotrauma.Items.Components;

namespace IEngine.Power;

static class VoltageReduction
{
    public static bool TryAverage(List<Connection> connections, out float voltage)
    {
        ArgumentNullException.ThrowIfNull(connections, "source");
        double sum = 0;
        long count = 0;
        foreach (Connection connection in connections)
        {
            GridInfo grid = connection.Grid;
            if (grid == null) { continue; }
            sum = count == 0 ? grid.Voltage : sum + grid.Voltage;
            count++;
        }
        voltage = count == 0 ? 0 : (float)(sum / count);
        return count != 0;
    }
}
