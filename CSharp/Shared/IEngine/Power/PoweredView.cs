using System.Collections;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;

namespace IEngine.Power;

sealed class PoweredView : IEnumerable<Powered>, IEnumerator<Powered>
{
    static readonly Stack<PoweredView> pool = new();

    List<ItemComponent> list;
    int index;

    public static IEnumerable<Powered> Of(Item item)
    {
        PoweredView view = pool.Count > 0 ? pool.Pop() : new PoweredView();
        view.list = item.componentsByType.TryGetValue(typeof(Powered), out List<ItemComponent> components) ? components : null;
        view.index = -1;
        return view;
    }

    public IEnumerator<Powered> GetEnumerator() => this;
    IEnumerator IEnumerable.GetEnumerator() => this;

    public Powered Current => (Powered)list[index];
    object IEnumerator.Current => Current;

    public bool MoveNext() => list != null && ++index < list.Count;

    public void Reset() => index = -1;

    public void Dispose()
    {
        list = null;
        pool.Push(this);
    }
}
