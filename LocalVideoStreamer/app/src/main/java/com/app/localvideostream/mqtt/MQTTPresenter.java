package com.app.localvideostream.mqtt;

import android.widget.Toast;

import com.app.localvideostream.MainActivity;
import com.app.localvideostream.util.Constants;

import org.eclipse.paho.android.service.MqttAndroidClient;
import org.eclipse.paho.client.mqttv3.IMqttActionListener;
import org.eclipse.paho.client.mqttv3.IMqttMessageListener;
import org.eclipse.paho.client.mqttv3.IMqttToken;
import org.eclipse.paho.client.mqttv3.MqttConnectOptions;
import org.eclipse.paho.client.mqttv3.MqttException;
import org.eclipse.paho.client.mqttv3.MqttMessage;

public class MQTTPresenter {

    private static final String TAG = "MQTTPresenter";
    private MainActivity view;

    public MQTTPresenter(MainActivity view) {
        this.view = view;
    }

    public void connectToMqtt(final MqttAndroidClient client) {
        try {
            MqttConnectOptions options = new MqttConnectOptions();
            options.setMqttVersion(MqttConnectOptions.MQTT_VERSION_DEFAULT);
            options.setUserName(Constants.username);
            options.setPassword(Constants.password.toCharArray());
            IMqttToken token = client.connect(options);
            token.setActionCallback(new IMqttActionListener() {
                @Override
                public void onSuccess(IMqttToken asyncActionToken) {
                    // We are connected
                    subscribeToMqttChannel(client);
                    view.onSuccess("Connected");
                }

                @Override
                public void onFailure(IMqttToken asyncActionToken, Throwable exception) {
                    // Something went wrong e.g. connection timeout or firewall problems
                    Toast.makeText(view.getBaseContext(), exception.getMessage(), Toast.LENGTH_LONG).show();
                }
            });

        } catch (MqttException e) {
            e.printStackTrace();
        }
    }

    public void subscribeToMqttChannel(MqttAndroidClient client) {
        try {
            IMqttToken subToken = client.subscribe(Constants.topic, Constants.qos, iMqttMessageListener);
        } catch (MqttException e) {
            e.printStackTrace();
        }
    }

    IMqttMessageListener iMqttMessageListener = new IMqttMessageListener() {
        @Override
        public void messageArrived(String topic, MqttMessage message) {
            view.receiveImage(message.getPayload());
        }
    };

    public void unSubscribeMqttChannel(MqttAndroidClient client) {
        try {
            IMqttToken unsubToken = client.unsubscribe(Constants.topic);
            unsubToken.setActionCallback(new IMqttActionListener() {
                @Override
                public void onSuccess(IMqttToken asyncActionToken) {
                    // The subscription could successfully be removed from the client
                }

                @Override
                public void onFailure(IMqttToken asyncActionToken,
                                      Throwable exception) {
                    // some error occurred, this is very unlikely as even if the client
                    // did not had a subscription to the topic the unsubscribe action
                    // will be successfully
                }
            });
        } catch (MqttException e) {
            e.printStackTrace();
        }
    }

    public void disconnectMqtt(MqttAndroidClient client) {
        try {
            IMqttToken token = client.disconnect();
            token.setActionCallback(new IMqttActionListener() {
                @Override
                public void onSuccess(IMqttToken asyncActionToken) {
                    // We are connected
                }

                @Override
                public void onFailure(IMqttToken asyncActionToken, Throwable exception) {
                    // Something went wrong e.g. connection timeout or firewall problems
                }
            });
        } catch (MqttException e) {
            e.printStackTrace();
        }
    }
}
