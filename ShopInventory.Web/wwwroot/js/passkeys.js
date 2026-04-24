window.passkeys = {
    isSupported: function () {
        return !!window.PublicKeyCredential && !!navigator.credentials && window.isSecureContext;
    },

    getContext: function () {
        return {
            origin: window.location.origin,
            rpId: window.location.hostname
        };
    },

    register: async function (optionsJson) {
        if (!this.isSupported()) {
            throw new Error('Passkeys require a supported browser over HTTPS or localhost.');
        }

        const publicKey = this.parseCreationOptions(optionsJson);
        const credential = await navigator.credentials.create({ publicKey: publicKey });

        if (!credential) {
            throw new Error('No passkey was created.');
        }

        return JSON.stringify(this.serializeAttestation(credential));
    },

    authenticate: async function (optionsJson) {
        if (!this.isSupported()) {
            throw new Error('Passkeys require a supported browser over HTTPS or localhost.');
        }

        const publicKey = this.parseRequestOptions(optionsJson);
        const assertion = await navigator.credentials.get({ publicKey: publicKey });

        if (!assertion) {
            throw new Error('No passkey assertion was returned.');
        }

        return JSON.stringify(this.serializeAssertion(assertion));
    },

    parseCreationOptions: function (optionsJson) {
        const options = JSON.parse(optionsJson);
        options.challenge = this.base64UrlToUint8Array(options.challenge);

        if (options.user && options.user.id) {
            options.user.id = this.base64UrlToUint8Array(options.user.id);
        }

        if (Array.isArray(options.excludeCredentials)) {
            options.excludeCredentials = options.excludeCredentials.map(descriptor => ({
                ...descriptor,
                id: this.base64UrlToUint8Array(descriptor.id)
            }));
        }

        return options;
    },

    parseRequestOptions: function (optionsJson) {
        const options = JSON.parse(optionsJson);
        options.challenge = this.base64UrlToUint8Array(options.challenge);

        if (Array.isArray(options.allowCredentials)) {
            options.allowCredentials = options.allowCredentials.map(descriptor => ({
                ...descriptor,
                id: this.base64UrlToUint8Array(descriptor.id)
            }));
        }

        return options;
    },

    serializeAttestation: function (credential) {
        const transports = typeof credential.response.getTransports === 'function'
            ? credential.response.getTransports()
            : [];

        return {
            id: credential.id,
            rawId: this.arrayBufferToBase64Url(credential.rawId),
            type: credential.type,
            authenticatorAttachment: credential.authenticatorAttachment || null,
            clientExtensionResults: credential.getClientExtensionResults ? credential.getClientExtensionResults() : {},
            response: {
                attestationObject: this.arrayBufferToBase64Url(credential.response.attestationObject),
                clientDataJSON: this.arrayBufferToBase64Url(credential.response.clientDataJSON),
                transports: transports
            }
        };
    },

    serializeAssertion: function (assertion) {
        return {
            id: assertion.id,
            rawId: this.arrayBufferToBase64Url(assertion.rawId),
            type: assertion.type,
            authenticatorAttachment: assertion.authenticatorAttachment || null,
            clientExtensionResults: assertion.getClientExtensionResults ? assertion.getClientExtensionResults() : {},
            response: {
                authenticatorData: this.arrayBufferToBase64Url(assertion.response.authenticatorData),
                clientDataJSON: this.arrayBufferToBase64Url(assertion.response.clientDataJSON),
                signature: this.arrayBufferToBase64Url(assertion.response.signature),
                userHandle: assertion.response.userHandle
                    ? this.arrayBufferToBase64Url(assertion.response.userHandle)
                    : null
            }
        };
    },

    arrayBufferToBase64Url: function (value) {
        const bytes = value instanceof Uint8Array ? value : new Uint8Array(value);
        let binary = '';
        for (let index = 0; index < bytes.byteLength; index++) {
            binary += String.fromCharCode(bytes[index]);
        }

        return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/g, '');
    },

    base64UrlToUint8Array: function (value) {
        const normalized = value.replace(/-/g, '+').replace(/_/g, '/');
        const padded = normalized + '==='.slice((normalized.length + 3) % 4);
        const binary = atob(padded);
        const bytes = new Uint8Array(binary.length);
        for (let index = 0; index < binary.length; index++) {
            bytes[index] = binary.charCodeAt(index);
        }

        return bytes;
    }
};