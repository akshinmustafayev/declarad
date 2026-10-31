namespace Declarad.Directory
{
    public sealed class DirectoryException : DeclaradException
    {
        public int ResultCode { get; }

        public DirectoryException(string message, int resultCode = 0, Exception? inner = null)
            : base(message, default, inner)
        {
            ResultCode = resultCode;
        }
    }
}
