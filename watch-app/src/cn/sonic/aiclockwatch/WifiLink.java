package cn.sonic.aiclockwatch;

import android.content.Context;
import android.net.*;
import java.net.Inet4Address;

public final class WifiLink {
    public final Network network;
    public final String ip;
    public final int prefix;
    WifiLink(Network n, String address, int bits) { network = n; ip = address; prefix = bits; }
    public String key() { return network + ":" + ip + "/" + prefix; }
    public static WifiLink current(Context context) {
        ConnectivityManager manager = (ConnectivityManager)context.getSystemService(Context.CONNECTIVITY_SERVICE);
        for (Network network : manager.getAllNetworks()) {
            NetworkCapabilities caps = manager.getNetworkCapabilities(network);
            LinkProperties props = manager.getLinkProperties(network);
            if (caps == null || props == null || !caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)) continue;
            for (LinkAddress address : props.getLinkAddresses())
                if (address.getAddress() instanceof Inet4Address && !address.getAddress().isLoopbackAddress())
                    return new WifiLink(network, address.getAddress().getHostAddress(), address.getPrefixLength());
        }
        return new WifiLink(null, "", 0);
    }
}
