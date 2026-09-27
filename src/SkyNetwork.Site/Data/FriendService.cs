using Dapper;

namespace SkyNetwork.Site.Data;

/// <summary>
/// Friends on the map: the members a member follows. One way, like following: the other person is not asked or
/// told (who is online is public anyway). Only confirmed members can be added.
/// </summary>
public sealed class FriendService(Database db, MemberService members)
{
    public const int Limit = 200;

    public IReadOnlyList<long> Ids(long cid)
    {
        using var c = db.Open();
        return c.Query<long>("SELECT friend_cid FROM friends WHERE cid = @cid ORDER BY created_at DESC, friend_cid", new { cid }).ToList();
    }

    public bool IsFriend(long cid, long friend)
    {
        using var c = db.Open();
        return c.ExecuteScalar<long>("SELECT COUNT(*) FROM friends WHERE cid = @cid AND friend_cid = @friend", new { cid, friend }) > 0;
    }

    /// <summary>The friends with their names and ratings, the most recently added first.</summary>
    public IReadOnlyList<Member> List(long cid) =>
        Ids(cid).Select(members.FindConfirmed).OfType<Member>().ToList();

    /// <summary>Adds a friend; returns an English error or null (adding someone twice is fine).</summary>
    public string? Add(long cid, long friend)
    {
        if (friend == cid) return "You cannot add yourself";
        if (members.FindConfirmed(friend) == null) return "No member with this CID";
        using var c = db.Open();
        if (c.ExecuteScalar<long>("SELECT COUNT(*) FROM friends WHERE cid = @cid AND friend_cid = @friend", new { cid, friend }) > 0) return null;
        if (c.ExecuteScalar<long>("SELECT COUNT(*) FROM friends WHERE cid = @cid", new { cid }) >= Limit) return "Your friends list is full";
        c.Execute("INSERT INTO friends (cid, friend_cid, created_at) VALUES (@cid, @friend, @now)", new { cid, friend, now = Database.Now() });
        return null;
    }

    public void Remove(long cid, long friend)
    {
        using var c = db.Open();
        c.Execute("DELETE FROM friends WHERE cid = @cid AND friend_cid = @friend", new { cid, friend });
    }
}
