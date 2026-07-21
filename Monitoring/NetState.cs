// Internet reachability, in increasing health.
enum NetStatus
{
    Unknown,      // not probed yet / monitoring off
    Offline,      // no usable network adapter at all
    NoInternet,   // adapter is up, but the public internet is unreachable (captive portal, DNS, etc.)
    Online        // a real HTTP probe to the internet succeeded
}

// Immutable snapshot handed to the UI: the status plus a short human-readable detail line.
record NetState(NetStatus Status, string Detail);
