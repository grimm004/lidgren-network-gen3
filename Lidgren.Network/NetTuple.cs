namespace Lidgren.Network;

// replace with BCL 4.0 Tuple<> when appropriate
internal struct NetTuple<A, B>(A item1, B item2)
{
	public A Item1 = item1;
	public B Item2 = item2;
}