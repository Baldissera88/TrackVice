namespace TrackVice.Tests
{
    public static class PublishTargetTests
    {
        const string Link = "https://contosotrackvice.blob.core.windows.net/trackvice-agent?sv=2022-11-02&sp=cw&sig=made-up";

        public static void Run()
        {
            PublishTarget a = PublishTarget.Decide("", Link, false, "");
            T.Eq(PublishTargetKind.SetUp, a.Kind, "storage link is set up");
            T.Eq("contosotrackvice", a.Account, "account from the link");
            T.Check(a.CanPublish, "set up can publish");

            PublishTarget b = PublishTarget.Decide("", "", false, "");
            T.Eq(PublishTargetKind.NotSetUp, b.Kind, "nothing is not set up");
            T.Check(!b.CanPublish && b.Reason.Length > 0, "not set up says why");

            PublishTarget c = PublishTarget.Decide("", Link, true, "");
            T.Eq(PublishTargetKind.UseLink, c.Kind, "another link");
            T.Check(!c.CanPublish, "empty box cannot publish");

            PublishTarget d = PublishTarget.Decide("", "", true, "  \"D:\\Updates\"  ");
            T.Eq("D:\\Updates", d.Link, "folder is trimmed");
            T.Check(d.CanPublish && d.Account == "", "folder can publish, no account");

            PublishTarget e = PublishTarget.Decide(Link, "", false, "");
            T.Eq(PublishTargetKind.SetUp, e.Kind, "saved link is set up");

            PublishTarget f = PublishTarget.Decide("D:\\Updates", Link, false, "");
            T.Eq(PublishTargetKind.UseLink, f.Kind, "saved folder shows in the box");
            T.Eq("D:\\Updates", f.Link, "saved folder is the link");

            T.Eq("", PublishTarget.AccountOf("\\\\fs01\\updates"), "share has no account");
        }
    }
}
