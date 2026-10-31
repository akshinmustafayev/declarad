namespace Declarad.Engine
{
    public enum ChangeAction
    {
        NoOp,
        Create,
        Update,
        Delete,
        /// <summary>The object no longer exists in the directory; only the state entry is dropped.</summary>
        Forget,
        /// <summary>An attachment moves to a different target: detach from the old one, attach to the new one.</summary>
        Replace,
    }
}
