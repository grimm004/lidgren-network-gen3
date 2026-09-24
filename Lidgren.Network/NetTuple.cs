namespace Lidgren.Network;

// replace with BCL 4.0 Tuple<> when appropriate
internal struct NetTuple<TA, TB>(TA item1, TB item2)
{
	public readonly TA Item1 = item1;
	public readonly TB Item2 = item2;
}