
namespace FispurEngine.API
{
    public interface IChessBot
    {
        string GetAuthor();
        string GetName();
        (Move move, int eval) Think(Board board, Timer timer, int maxDepth = -1);
    }
}
