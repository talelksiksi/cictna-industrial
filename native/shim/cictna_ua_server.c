/*
 * cictna_ua_server.c: the OPC UA server behind cictna_ua_server_*.
 *
 * Phase 1 uses it as the test server the client is verified against (every built-in type,
 * arrays, folders, operation limits, security). Phase 5 builds the PLC simulator on it.
 * Same model as the client: one managed worker thread owns the object and drives
 * cictna_ua_server_iterate; local reads/writes/adds are made from that same thread.
 */
#include "cictna_ua_internal.h"

#include <open62541/plugin/accesscontrol_default.h>
#include <open62541/server.h>
#include <open62541/server_config_default.h>

#include <stdlib.h>
#include <string.h>

struct cictna_ua_server {
    UA_Server *server;
    int started;
    cictna_log_queue log;
};

uint32_t cictna_ua_server_new(const cictna_ua_server_options *o, cictna_ua_server **out) {
    *out = NULL;
    if(!o || o->struct_size != sizeof(cictna_ua_server_options))
        return UA_STATUSCODE_BADINVALIDARGUMENT;

    cictna_ua_server *s = (cictna_ua_server *)calloc(1, sizeof(cictna_ua_server));
    if(!s)
        return UA_STATUSCODE_BADOUTOFMEMORY;
    cictna_logq_init(&s->log, 1024, o->log_level);

    UA_ServerConfig sc;
    memset(&sc, 0, sizeof(sc));
    sc.logging = cictna_logq_logger(&s->log);

    UA_StatusCode rc;
#ifdef UA_ENABLE_ENCRYPTION
    if(o->certificate && o->certificate_len && o->private_key && o->private_key_len) {
        /* None plus all secure policies on one port. Empty trust list: client certificates are
         * not checked yet (Phase 3 adds trust lists for both client and server). */
        UA_ByteString cert = {o->certificate_len, (UA_Byte *)(uintptr_t)o->certificate};
        UA_ByteString key = {o->private_key_len, (UA_Byte *)(uintptr_t)o->private_key};
        rc = UA_ServerConfig_setDefaultWithSecurityPolicies(&sc, o->port, &cert, &key,
                                                            NULL, 0, NULL, 0, NULL, 0);
    } else
#endif
    {
        rc = UA_ServerConfig_setMinimal(&sc, o->port, NULL);
    }
    if(rc != UA_STATUSCODE_GOOD)
        goto fail;

    if(o->application_uri) {
        UA_String_clear(&sc.applicationDescription.applicationUri);
        sc.applicationDescription.applicationUri = cictna_string(o->application_uri);
    }
    if(o->application_name) {
        UA_LocalizedText_clear(&sc.applicationDescription.applicationName);
        sc.applicationDescription.applicationName = UA_LOCALIZEDTEXT_ALLOC("", o->application_name);
    }
    if(o->max_nodes_per_read) sc.maxNodesPerRead = o->max_nodes_per_read;
    if(o->max_nodes_per_write) sc.maxNodesPerWrite = o->max_nodes_per_write;
    if(o->max_nodes_per_browse) sc.maxNodesPerBrowse = o->max_nodes_per_browse;
    if(o->max_references_per_node) sc.maxReferencesPerNode = o->max_references_per_node;
    /* How many values a monitored item may queue between two Publish requests. Clients ask for a
     * queue size and the server revises it down to this. A larger limit lets a subscription ride
     * out a longer outage without the server discarding values (it marks discards with the
     * Overflow bit). */
    if(o->max_monitored_item_queue_size) sc.queueSizeLimits.max = o->max_monitored_item_queue_size;

    /* Access control: replace the default (anonymous only) with our settings. */
    if(sc.accessControl.clear)
        sc.accessControl.clear(&sc.accessControl);
    UA_UsernamePasswordLogin login;
    size_t login_count = 0;
    if(o->username) {
        login.username = UA_STRING((char *)(uintptr_t)o->username);
        login.password = UA_STRING((char *)(uintptr_t)(o->password ? o->password : ""));
        login_count = 1;
    }
    /* The user token policy for passwords: the strongest registered policy, so passwords are
     * never sent in clear text when encryption is available. */
    const UA_String *token_policy = sc.securityPoliciesSize > 0
        ? &sc.securityPolicies[sc.securityPoliciesSize - 1].policyUri : NULL;
    rc = UA_AccessControl_default(&sc, o->allow_anonymous ? true : false, token_policy,
                                  login_count, &login);
    if(rc != UA_STATUSCODE_GOOD)
        goto fail;

    s->server = UA_Server_newWithConfig(&sc); /* moves sc into the server */
    if(!s->server) {
        cictna_logq_clear(&s->log);
        free(s);
        return UA_STATUSCODE_BADOUTOFMEMORY;
    }
    *out = s;
    return UA_STATUSCODE_GOOD;

fail:
    UA_ServerConfig_clear(&sc);
    cictna_logq_clear(&s->log);
    free(s);
    return rc;
}

uint32_t cictna_ua_server_start(cictna_ua_server *s) {
    UA_StatusCode rc = UA_Server_run_startup(s->server);
    if(rc == UA_STATUSCODE_GOOD)
        s->started = 1;
    return rc;
}

uint32_t cictna_ua_server_iterate(cictna_ua_server *s, uint32_t *next_ms) {
    UA_UInt16 wait = UA_Server_run_iterate(s->server, false);
    if(next_ms)
        *next_ms = wait;
    return UA_STATUSCODE_GOOD;
}

uint32_t cictna_ua_server_stop(cictna_ua_server *s) {
    if(!s->started)
        return UA_STATUSCODE_GOOD;
    s->started = 0;
    return UA_Server_run_shutdown(s->server);
}

void cictna_ua_server_delete(cictna_ua_server *s) {
    if(!s)
        return;
    if(s->started)
        UA_Server_run_shutdown(s->server);
    UA_Server_delete(s->server);
    cictna_logq_clear(&s->log);
    free(s);
}

uint32_t cictna_ua_server_add_namespace(cictna_ua_server *s, const char *uri, uint16_t *index) {
    if(!uri)
        return UA_STATUSCODE_BADINVALIDARGUMENT;
    *index = UA_Server_addNamespace(s->server, uri);
    return UA_STATUSCODE_GOOD;
}

uint32_t cictna_ua_server_add_node(cictna_ua_server *s, const uint8_t *item, size_t item_len,
                                   uint8_t **node_id, size_t *node_id_len) {
    *node_id = NULL;
    *node_id_len = 0;
    UA_StatusCode rc;
    UA_AddNodesItem *it = (UA_AddNodesItem *)cictna_decode(&UA_TYPES[UA_TYPES_ADDNODESITEM], item, item_len, &rc);
    if(!it)
        return rc;

    /* open62541 decodes known structures inside ExtensionObjects, so the attributes arrive as
     * a decoded VariableAttributes or ObjectAttributes. Anything else is rejected. */
    const UA_ExtensionObject *attrs = &it->nodeAttributes;
    UA_NodeId out_id = UA_NODEID_NULL;
    if(it->nodeClass == UA_NODECLASS_VARIABLE &&
       attrs->encoding == UA_EXTENSIONOBJECT_DECODED &&
       attrs->content.decoded.type == &UA_TYPES[UA_TYPES_VARIABLEATTRIBUTES]) {
        rc = UA_Server_addVariableNode(s->server, it->requestedNewNodeId.nodeId,
                                       it->parentNodeId.nodeId, it->referenceTypeId, it->browseName,
                                       it->typeDefinition.nodeId,
                                       *(const UA_VariableAttributes *)attrs->content.decoded.data,
                                       NULL, &out_id);
    } else if(it->nodeClass == UA_NODECLASS_OBJECT &&
              attrs->encoding == UA_EXTENSIONOBJECT_DECODED &&
              attrs->content.decoded.type == &UA_TYPES[UA_TYPES_OBJECTATTRIBUTES]) {
        rc = UA_Server_addObjectNode(s->server, it->requestedNewNodeId.nodeId,
                                     it->parentNodeId.nodeId, it->referenceTypeId, it->browseName,
                                     it->typeDefinition.nodeId,
                                     *(const UA_ObjectAttributes *)attrs->content.decoded.data,
                                     NULL, &out_id);
    } else {
        rc = UA_STATUSCODE_BADNODEATTRIBUTESINVALID;
    }
    UA_delete(it, &UA_TYPES[UA_TYPES_ADDNODESITEM]);
    if(rc == UA_STATUSCODE_GOOD)
        rc = cictna_encode(&out_id, &UA_TYPES[UA_TYPES_NODEID], node_id, node_id_len);
    UA_NodeId_clear(&out_id);
    return rc;
}

uint32_t cictna_ua_server_write(cictna_ua_server *s, const uint8_t *write_value, size_t len) {
    UA_StatusCode rc;
    UA_WriteValue *wv = (UA_WriteValue *)cictna_decode(&UA_TYPES[UA_TYPES_WRITEVALUE], write_value, len, &rc);
    if(!wv)
        return rc;
    rc = UA_Server_write(s->server, wv);
    UA_delete(wv, &UA_TYPES[UA_TYPES_WRITEVALUE]);
    return rc;
}

uint32_t cictna_ua_server_read(cictna_ua_server *s, const uint8_t *read_value_id, size_t len,
                               int32_t timestamps, uint8_t **data_value, size_t *data_value_len) {
    *data_value = NULL;
    *data_value_len = 0;
    UA_StatusCode rc;
    UA_ReadValueId *rv = (UA_ReadValueId *)cictna_decode(&UA_TYPES[UA_TYPES_READVALUEID], read_value_id, len, &rc);
    if(!rv)
        return rc;
    UA_DataValue dv = UA_Server_read(s->server, rv, (UA_TimestampsToReturn)timestamps);
    UA_delete(rv, &UA_TYPES[UA_TYPES_READVALUEID]);
    rc = cictna_encode(&dv, &UA_TYPES[UA_TYPES_DATAVALUE], data_value, data_value_len);
    UA_DataValue_clear(&dv);
    return rc;
}

int32_t cictna_ua_server_poll_log(cictna_ua_server *s, int32_t *level, int32_t *category,
                                  const char **message, uint32_t *dropped) {
    return cictna_logq_pop(&s->log, level, category, message, dropped);
}
