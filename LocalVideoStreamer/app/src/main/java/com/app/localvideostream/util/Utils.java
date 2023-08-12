package com.app.localvideostream.util;

import android.content.Context;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;

public class Utils {
    public static Bitmap convertByteToBitmap(Context context, byte[] data) {
        Bitmap bmp = BitmapFactory.decodeByteArray(data, 0, data.length);
        return bmp;
    }

    public static byte[] subArray(byte[] b, int offset, int length) {
        byte[] sub = new byte[length];
        for (int i = offset; i < offset + length; i++) {
            try {
                sub[i - offset] = b[i];
            } catch (Exception e) {

            }
        }
        return sub;
    }
}
