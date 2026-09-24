namespace CL.Storage.Providers;

/// <summary>
/// Marks a backend whose <c>MoveAsync</c> with <c>Overwrite</c> onto an existing file keeps the replaced file:
/// it is renamed to a backup on the server first, restored if the replacement fails, and removed only after the
/// replacement committed (a backup that could not be removed is reported with <c>destinationState=complete</c> and
/// <c>leftBehind</c>). A caller promoting a staged file onto such a backend needs no backup copy of its own, which
/// on these servers would be a download and re-upload through the client (FTP, SFTP).
/// </summary>
internal interface IStorageRestoringReplace;
