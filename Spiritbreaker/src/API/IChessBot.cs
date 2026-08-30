
namespace Spiritbreaker.API
{
    public interface IChessBot
    {
        string GetAuthor();
        string GetName();
        Move Think(Board board, Timer timer);
    }
}
