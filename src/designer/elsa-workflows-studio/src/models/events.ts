﻿import {ActivityModel} from "./view";
import {ActivityDescriptor} from "./domain";

export const EventTypes = {
  ActivityEditor: {
    Show: 'show-activity-editor',
    Appearing: 'activity-editor-appearing',
    Disappearing: 'activity-editor-disappearing'
  },
  ShowActivityPicker: 'show-activity-picker',
  ShowWorkflowSettings: 'show-workflow-settings',
  ActivityPicked: 'activity-picked',
  UpdateActivity: 'update-activity',
  UpdateWorkflowSettings: 'update-workflow-settings',
  WorkflowModelChanged: 'workflow-model-changed',
  ActivityDesignDisplaying: 'activity-design-displaying',
  ActivityDescriptorDisplaying: 'activity-descriptor-displaying',
  ActivityPluginUpdated: 'activity-plugin-updated',
  ActivityPluginValidating: 'activity-plugin-validating',
  WorkflowPublished: 'workflow-published',
  WorkflowRetracted: 'workflow-retracted',
  WorkflowImported: 'workflow-imported',
  WorkflowUpdated: 'workflow-updated',
  HttpClientConfigCreated: 'http-client-config-created',
  HttpClientCreated: 'http-client-created',
  WorkflowInstanceBulkActionsLoading: 'workflow-instance-bulk-actions-loading',
  ShowConfirmDialog: 'show-confirm-dialog',
  HideConfirmDialog: 'hide-confirm-dialog',
  ShowModalDialog: 'show-modal-dialog',
  HideModalDialog: 'hide-modal-dialog',
  ShowToastNotification: 'show-toast-notification',
  HideToastNotification: 'hide-toast-notification',
  ConfigureFeature: 'configure-feature',
  DashboardLoadingMenu: 'dashboard.loading-menu',
  WorkflowRegistryLoadingColumns: 'workflow-registry.loading-columns',
  WorkflowRegistryUpdating: 'workflow-registry.updating',
  WorkflowRegistryUpdated: 'workflow-registry.updated',
  ClipboardPermissionDenied: 'clipboard-permission-denied',
  ClipboardCopied: 'clipboard-copied',
  PasteActivity: 'paste-activity',
  TestActivityMessageReceived: 'test-activity-message-received',
  FlyoutPanelTabSelected: 'flyout-panel-tab-selected',
  ComponentLoadingCustomButton: 'component-loading-custom-button',
  ComponentCustomButtonClick: 'component-custom-button-click'
};

export interface AddActivityEventArgs {
  sourceActivityId?: string;
}

export interface ActivityPickedEventArgs {
  activityType: string;
}

export interface ActivityDesignDisplayContext {
  activityModel: ActivityModel;
  activityDescriptor: ActivityDescriptor;
  activityIcon: any;
  displayName?: string;
  bodyDisplay: string;
  outcomes: Array<string>;
}

export interface ActivityUpdatedContext { 
  activityModel: ActivityModel,
  data?: string
}

export interface ActivityValidatingContext { 
  activityType: string
  prop: string,
  value?: string,
  isValidated: boolean,
  data: any,
  isValid: boolean
}

export interface ActivityDescriptorDisplayContext {
  activityDescriptor: ActivityDescriptor;
  activityIcon: any;
}

export interface ConfigureDashboardMenuContext {
  data: any;
}

export interface ConfigureWorkflowRegistryColumnsContext {
  data: any;
}

export interface ConfigureWorkflowRegistryUpdatingContext {
  params: any;
}

export interface ConfigureComponentCustomButtonContext {
  component: string;
  activityType: string;
  prop: string;
  data?: any;
}

export interface ComponentCustomButtonClickContext {
  component: string;
  activityType: string;
  prop: string;
  params: any;
}
