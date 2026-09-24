# needs-review round 3: what was done

Branch `feat/storage-needs`, on top of `aaf4afd`. Every finding in sections A, B, and D of StorageHub's
`needs-review.md` is below. Each fixed finding has at least one test tagged `// needs-review <id>` that
fails on the code before the fix and passes after (checked by running the new tests against the base
source; tests that use new API could not compile there and fail for that reason). Some cited tests are
partners that pass on the old code as well (round 4 found `Sides_keeping_different_checksums_download_only_one_side`
(B60), `A_connection_failure_while_hashing_still_fails_the_comparison` (B57), `Every_public_enum_has_distinct_values`
(A14), and two `Rename_candidates` cases); each has a partner in its row that fails there. Section F is a proposal only:
[storagehub-needs-proposal.md](storagehub-needs-proposal.md).

## Done criteria

| Check | Result |
|---|---|
| `dotnet build CL.Storage -c Release` | 0 warnings |
| `dotnet test tests/Storage.Tests` | all pass (see the final run in the hand-off message) |
| Live suite, every server incl. `webdav-mtls` | all pass, none skipped |
| Acceptance program (`eng/cl-storage-acceptance`) | all 9 PASS, "All accepted.", exit code 0 |
| Doc samples | all 49 C# samples in README and `docs/libs/storage` compile |

## Where a guarantee is met differently than asked (not weakened, reported)

- **MinIO and Swift ignore `If-Match` on DELETE** (and MinIO on `CopyObject`). A pinned native move there
  deletes the source after checking it just before, and create-only copies on MinIO report
  `ConditionEnforcement = CheckedBeforeCommit`. `S3ConnectionConfig.ConditionalRequests` (`Auto` probes once
  per connection) decides it per server; AWS stays `Atomic`.
- **Swift** no longer claims `ConditionalUpdate`/`ConditionalDelete`: its conditions are checked just before.
- **WebDAV** no longer claims `AtomicMove` (a `MOVE` can end in `207`); folder moves relay.
- **File upload/download queue jobs** count as "destination may be written" once running, so a crash leaves
  them `Interrupted` rather than re-queued (the queue cannot see their commit point).
- **Local ETags** are weak (`W/"…"`); .NET gives no file id or change time without a handle per file, so
  collisions on coarse file-system clocks remain possible and are documented.

## Findings

| Id | Result | Commit(s) | Tests (class.method) / where documented |
|---|---|---|---|
| A1 | fixed | 12c0f17 | NeedsReviewTransferTests.A_complete_directory_move_deletes_the_source_file_by_file; NeedsReviewTransferTests.A_directory_move_keeps_files_added_or_changed_on_the_source_during_the_copy<br>*per-file conditional source delete, emptied folders non-recursively; NeedsReconciliation with sourceChanged/sourceAdded* |
| A2 | fixed | 12c0f17 (library), 2ee7489, 96f6cd3, f895d80 (S3, Azure/GCS, Swift), 522fa83 (sync rename) | NeedsReviewProviderLiveTests.A_GCS_copy_pinned_to_an_old_ETag_is_refused_and_a_move_keeps_nothing_behind; NeedsReviewProviderLiveTests.A_Swift_copy_is_pinned_to_the_source_read_and_a_create_only_copy_works; NeedsReviewProviderLiveTests.An_Azure_copy_pinned_to_an_old_ETag_is_refused_and_a_move_keeps_nothing_behind; NeedsReviewProviderLiveTests.An_S3_copy_pinned_to_an_old_ETag_is_refused_and_a_move_deletes_only_what_it_copied; NeedsReviewProviderS3Tests.A_directory_move_keeps_a_file_changed_after_it_was_listed; NeedsReviewProviderS3Tests.A_file_move_deletes_only_the_copied_object_with_If_Match; NeedsReviewProviderS3Tests.A_move_keeps_a_source_rewritten_after_the_copy_and_reports_the_destination_committed; NeedsReviewProviderS3Tests.Copy_honours_the_expected_source_ETag_and_version; NeedsReviewProviderTests.A_local_copy_or_move_honours_the_expected_source_ETag_and_refuses_versions; NeedsReviewSyncLiveTests.Keep_both_on_s3_keeps_both_versions; NeedsReviewSyncTests.A_conflict_rename_is_pinned_to_the_version_planned; NeedsReviewSyncTests.A_version_written_after_the_rename_check_is_not_moved_aside; NeedsReviewSyncTests.Where_a_move_cannot_be_pinned_the_rename_is_a_pinned_copy_and_a_conditional_delete; NeedsReviewTransferTests.A_native_move_that_copies_and_deletes_is_pinned_to_the_version_read_and_relays_when_it_cannot_be<br>*pinned native copy/move; relay on Unsupported. On MinIO and Swift the pinned source delete is checked just before (servers ignore If-Match on DELETE): documented* |
| A3 | fixed | 12c0f17 (merge through relay), a04215a, 75a8725, 4a7f2d7 (SFTP/FTP/WebDAV refuse) | NeedsReviewProviderLiveTests.A_WebDAV_move_or_copy_onto_an_existing_collection_is_refused; NeedsReviewProviderLiveTests.An_FTP_move_onto_an_existing_directory_is_refused; NeedsReviewProviderLiveTests.An_SFTP_move_onto_an_existing_directory_is_refused; NeedsReviewProviderLiveTests.R3_8_an_SFTP_folder_moved_onto_an_existing_folder_keeps_what_the_folder_held; NeedsReviewProviderWebDavTests.A_move_or_copy_onto_an_existing_collection_is_refused_without_a_request; NeedsReviewTransferTests.A_directory_moved_onto_an_existing_directory_merges_instead_of_replacing_it<br>*acceptance R3-8* |
| A4 | fixed | 3d0825a, 12c0f17, cc846bc (queue replay) | NeedsReviewQueueTests.A_stored_resume_token_is_not_replayed_for_a_job_that_must_not_overwrite; NeedsReviewTransferTests.A_resume_token_does_not_turn_on_overwrite<br>*the coordinator's own guard has no test of its own: `StorageTransferOptions.Validate` refuses first* |
| A5 | fixed | 3b8bf58, 12c0f17 | NeedsReviewTransferTests.A_destination_that_does_not_hold_the_committed_length_always_fails_even_without_verify; NeedsReviewTransferTests.Two_transfers_of_one_source_to_one_destination_do_not_write_into_one_part_file<br>*one writer per part file; size/digest mismatch after promote always fails (NeedsReconciliation, no rollback)* |
| A6 | fixed | 3b8bf58 | NeedsReviewTransferTests.A_part_file_whose_size_cannot_be_read_is_kept_and_not_appended_after |
| A7 | fixed | 3b8bf58 | NeedsReviewTransferTests.A_fully_staged_resume_does_not_open_the_source_at_its_end |
| A8 | fixed | 12c0f17 | NeedsReviewTransferTests.A_directory_rollback_does_not_undo_a_file_someone_changed_after_it_was_committed; NeedsReviewTransferTests.A_verify_mismatch_after_commit_is_reported_and_never_rolled_back_over_another_write |
| A9 | fixed | 12c0f17 | NeedsReviewTransferTests.A_destination_deleted_while_its_condition_is_checked_is_not_brought_back |
| A10 | fixed | 2ee7489 | NeedsReviewProviderLiveTests.R2_2_an_S3_create_only_copy_keeps_its_properties_a_plain_ETag_and_the_server_MD5; NeedsReviewProviderLiveTests.The_S3_condition_probe_matches_what_the_server_does; NeedsReviewProviderS3Tests.A_copy_above_the_single_request_limit_pins_every_part_carries_properties_and_runs_parts_in_parallel; NeedsReviewProviderS3Tests.A_create_only_copy_below_5_GiB_is_one_pinned_CopyObject_that_keeps_properties_and_a_plain_ETag; NeedsReviewProviderS3Tests.A_source_replaced_during_a_multipart_copy_fails_the_copy_and_aborts_it; NeedsReviewProviderS3Tests.Copy_and_delete_enforcement_is_probed_once_per_connection_and_cleans_up<br>*one CopyObject below 5 GiB; pinned parallel parts above. MinIO reports CheckedBeforeCommit (probe); acceptance R2-2* |
| A11 | fixed | 2ee7489, a04215a, 75a8725 (providers), 3b8bf58, 12c0f17 (library), a0c48d7, 4884e16 (merge) | NeedsReviewProviderLiveTests.An_FTP_replace_whose_backup_cannot_be_removed_reports_the_destination_committed_and_the_backup; NeedsReviewProviderLiveTests.An_SFTP_replace_whose_backup_cannot_be_removed_reports_the_destination_committed_and_the_backup; NeedsReviewProviderS3Tests.A_cancel_after_the_copy_still_deletes_the_source; NeedsReviewProviderS3Tests.A_move_whose_source_delete_fails_reports_the_destination_committed_and_what_was_left; NeedsReviewSyncTests.A_promote_that_committed_and_then_failed_is_an_applied_copy_with_its_baseline_entry; NeedsReviewTransferTests.A_native_move_whose_source_stayed_or_that_stopped_part_way_needs_reconciliation; NeedsReviewTransferTests.A_provider_error_after_the_commit_is_a_committed_transfer_with_its_leftover; NeedsReviewTransferTests.Staged_uploads_and_streamed_writes_treat_a_provider_error_after_the_commit_as_committed |
| A12 | fixed | 3b8bf58 | NeedsReviewTransferTests.A_cancelled_write_aborts_the_stream_so_a_retry_cannot_duplicate_bytes |
| A13 | fixed | 12c0f17 | NeedsReviewTransferTests.A_native_copy_that_succeeded_is_not_reported_cancelled_when_the_cancel_lands_after_it |
| A14 | fixed | beb23f1 | NeedsReviewEnumTests.Every_public_enum_has_distinct_values; NeedsReviewEnumTests.Transfer_states_keep_the_numbers_4_8_93_stored<br>*acceptance R2-3* |
| A15 | fixed | cc846bc | NeedsReviewQueueTests.A_cancel_or_pause_after_a_move_committed_keeps_needs_reconciliation |
| A16 | fixed | cc846bc | NeedsReviewQueueTests.A_directory_copy_never_records_an_earlier_phase_after_committing; NeedsReviewQueueTests.Upload_and_download_jobs_record_that_the_destination_may_be_written<br>*file upload/download jobs record Committing when they start, so after a crash they are Interrupted (documented)* |
| A17 | fixed | cc846bc | NeedsReviewQueueTests.Pruning_does_not_remove_a_job_another_worker_retried; NeedsReviewQueueTests.Removing_a_job_another_process_changed_is_refused; NeedsReviewQueueTests.Removing_a_queued_job_as_it_would_start_raises_job_removed_once_and_it_never_runs; NeedsReviewQueueTests.Removing_a_running_job_reports_a_store_failure_and_keeps_the_job<br>*conditional store RemoveAsync(id, revision, lease)* |
| A18 | fixed | cc846bc | NeedsReviewQueueTests.A_cancel_during_a_refused_claim_is_not_dropped; NeedsReviewQueueTests.A_cancel_while_the_outcome_is_saved_stops_the_retry |
| A19 | fixed | 522fa83 | NeedsReviewSyncLiveTests.A_folder_spelled_differently_on_local_and_s3_gets_no_second_folder; NeedsReviewSyncLiveTests.A_folder_spelled_differently_on_local_and_sftp_gets_no_second_folder; NeedsReviewSyncTests.A_kept_conflict_copy_goes_into_each_sides_own_folder; NeedsReviewSyncTests.A_new_file_under_a_folder_spelled_differently_lands_in_the_destinations_folder; NeedsReviewSyncTests.A_two_way_copy_into_a_case_sensitive_source_uses_the_sources_folder; NeedsReviewSyncTests.Entries_use_the_sources_folder_spelling_and_a_folder_comes_right_before_its_contents<br>*acceptance R2-1* |
| A20 | fixed | 522fa83 | NeedsReviewSyncTests.A_delete_that_could_not_read_the_file_fails_and_is_not_undone_later; NeedsReviewSyncTests.A_transient_failure_to_read_the_source_is_retried_not_reported_stale |
| A21 | fixed | 522fa83 | NeedsReviewSyncTests.A_blocked_delete_versus_modify_conflict_stays_a_conflict_where_case_is_ignored; NeedsReviewSyncTests.A_withheld_delete_keeps_its_baseline_entry_where_case_is_ignored |
| A22 | fixed | 522fa83 | NeedsReviewCompareTests.An_excluded_folder_is_kept_as_one_prefix_not_as_every_path_inside_it; NeedsReviewSyncTests.Excluding_a_folder_excludes_everything_inside_it<br>*acceptance R3-5* |
| A23 | fixed | 522fa83 | NeedsReviewCompareTests.A_recursive_listing_without_hidden_items_leaves_out_what_hidden_folders_hold; NeedsReviewSyncTests.Leaving_hidden_items_out_leaves_out_the_contents_of_hidden_folders_and_keeps_folders_holding_them<br>*acceptance R3-6* |
| A24 | fixed | 4a6c909 (Local), 522fa83 (sync) | NeedsReviewProviderTests.A_recursive_listing_does_not_descend_into_a_directory_link; NeedsReviewProviderTests.An_app_execution_alias_is_a_file_not_a_link; NeedsReviewSyncTests.A_file_hidden_by_attribute_at_the_source_keeps_its_copy_under_mirror; NeedsReviewSyncTests.Mirror_never_deletes_what_the_source_left_out_for_what_it_is<br>*acceptance R3-7 still passes* |
| A25 | fixed | 522fa83 | NeedsReviewSyncTests.A_cancel_landing_during_the_promote_still_records_the_copy_and_sets_its_time; NeedsReviewSyncTests.A_copy_whose_read_back_fails_is_recorded_with_what_was_written; NeedsReviewSyncTests.A_same_size_write_by_someone_else_right_after_the_copy_is_not_recorded_as_ours |
| A26 | fixed | f895d80 | NeedsReviewProviderLiveTests.A_Swift_upload_with_a_condition_is_checked_by_the_backend_itself; NeedsReviewProviderLiveTests.R3_9_a_Swift_upload_with_a_wrong_If_Match_is_refused_and_the_file_kept<br>*acceptance R3-9* |
| A27 | fixed | 686a556 | NeedsReviewProviderTlsTests.A_connection_never_asked_for_a_certificate_records_nothing; NeedsReviewProviderTlsTests.A_connection_that_fails_after_the_certificate_reply_records_a_refusal; NeedsReviewProviderTlsTests.A_connection_whose_certificate_was_accepted_records_no_refusal_when_it_drops_later; NeedsReviewProviderTlsTests.An_OpenSSL_error_on_an_established_connection_is_a_lost_connection_but_during_the_handshake_a_TLS_failure; NeedsReviewProviderTlsTests.An_SChannel_record_error_on_an_established_connection_is_a_lost_connection; NeedsReviewProviderTlsTests.An_SSPI_Negotiate_error_is_not_a_refused_client_certificate; NeedsReviewProviderTlsTests.An_ordinary_drop_during_an_attempt_with_a_certificate_request_stays_a_transient_lost_connection |
| A28 | fixed | 686a556, 2e72849 (docs) | NeedsReviewProviderTlsTests.Key_storage_is_ephemeral_only_where_the_platform_supports_it<br>*macOS uses a temporary keychain; docs corrected* |
| A29 | documented | 2e72849 | NeedsReviewSyncTests.A_sync_cancelled_while_applying_returns_success_with_cancelled_and_one_cancelled_while_planning_throws<br>*behaviour kept (success with Cancelled=true) and pinned by a test; CHANGELOG, MIGRATION, README, errors-events.md, index.md, transfers.md corrected* |
| B1 | fixed | 3d0825a, 3b8bf58, 12c0f17 | NeedsReviewTransferApiTests.A_skipped_link_says_why_it_was_skipped; NeedsReviewTransferTests.Moving_a_single_link_that_is_skipped_leaves_it_in_place |
| B2 | fixed | 3d0825a, 3b8bf58, 12c0f17 | NeedsReviewTransferTests.A_resume_into_a_folder_the_transfer_created_fails_cleanly_with_a_token |
| B3 | fixed | 3d0825a, 3b8bf58, 12c0f17 | ReviewTransferTests.A_tampered_resume_token_is_not_followed<br>*a foreign file named by a token is ignored and never deleted; staging deletes non-recursive* |
| B4 | fixed | 3d0825a, 3b8bf58, 12c0f17 | NeedsReviewTransferTests.A_source_without_identity_never_gets_a_token_for_a_private_staging_object |
| B5 | fixed | 3d0825a, 3b8bf58, 12c0f17 | NeedsReviewTransferTests.ExpectedSha256_without_Verify_still_checks_the_destination_after_the_commit |
| B6 | fixed | 3d0825a, 3b8bf58, 12c0f17 | NeedsReviewTransferTests.A_throwing_provider_leaves_no_staging_or_backup_and_is_reported |
| B7 | fixed | 3d0825a, 3b8bf58, 12c0f17 | NeedsReviewTransferTests.A_failed_promote_keeps_the_resumable_part_file_for_a_token |
| B8 | fixed | 3d0825a, 3b8bf58, 12c0f17 | NeedsReviewTransferTests.Staged_uploads_and_streamed_writes_pass_their_condition_to_the_providers_move<br>*condition passed to the provider's move; the enforcement level is not returned by uploads (Result<StorageItem>): documented* |
| B9 | documented | 3d0825a (XML doc), 2e72849 | —<br>*SourceIdentity must change with the content; a path only together with SourceLastModified* |
| B10 | fixed | 3d0825a, 3b8bf58, 12c0f17 | NeedsReviewTransferTests.A_conditional_policy_judges_a_pinned_version_by_its_own_size |
| B11 | fixed | 3d0825a, 3b8bf58, 12c0f17 | NeedsReviewTransferTests.Rename_candidates_count_on_and_never_end_in_a_dot; NeedsReviewTransferTests.Rename_takes_the_next_name_when_the_chosen_one_is_taken_before_the_commit_or_is_a_folder |
| B12 | fixed | 3d0825a, 3b8bf58, 12c0f17 | NeedsReviewTransferTests.Validation_refuses_a_condition_with_another_policy_and_empty_source_identities |
| B13 | fixed | 3d0825a, 3b8bf58, 12c0f17 | NeedsReviewTransferTests.A_cancelled_directory_transfer_whose_rollback_failed_needs_reconciliation |
| B14 | fixed | 3d0825a, 3b8bf58, 12c0f17 | NeedsReviewTransferTests.A_staged_upload_failure_keeps_the_providers_details |
| B15 | fixed | 3d0825a, 3b8bf58, 12c0f17 | NeedsReviewTransferTests.The_already_complete_check_reads_the_source_without_progress_or_speed_limits |
| B16 | fixed | 3d0825a, 3b8bf58, 12c0f17 | NeedsReviewTransferTests.Appending_to_a_part_file_respects_the_destinations_upload_limit |
| B17 | fixed | 12c0f17, a04215a, 75a8725 | NeedsReviewProviderLiveTests.An_SFTP_copy_works_with_one_session_and_the_replace_keeps_its_own_backup; NeedsReviewTransferApiTests.A_relay_on_a_connection_limited_to_one_session_fails_at_once; NeedsReviewTransferTests.Overwriting_on_a_connection_without_server_side_copy_makes_no_client_side_backup<br>*no client-side backup on FTP/SFTP; same-connection relay with MaxSessions=1 fails at once; FTP relay needs 2 sessions (documented)* |
| B18 | fixed | 4a7f2d7, 4884e16 | NeedsReviewProviderWebDavTests.A_collection_move_answered_with_207_is_a_partial_failure; NeedsReviewTransferTests.A_native_move_whose_source_stayed_or_that_stopped_part_way_needs_reconciliation<br>*207 is partial_failure -> NeedsReconciliation; WebDAV no longer claims AtomicMove* |
| B19 | fixed | f895d80 | NeedsReviewProviderLiveTests.A_versioned_Swift_container_serves_sync_copies_and_cross_connection_moves |
| B20 | fixed | 12c0f17, 2e72849 | NeedsReviewTransferTests.Discarding_metadata_is_never_left_to_a_native_copy<br>*Discard never native; links: a native rename moves links as they are (documented)* |
| B21 | fixed | 2ee7489 | NeedsReviewProviderS3Tests.A_completed_multipart_upload_whose_reply_was_lost_succeeds; NeedsReviewProviderS3Tests.A_lost_completion_reply_with_a_different_object_at_the_key_still_fails |
| B22 | fixed | a04215a | NeedsReviewProviderLiveTests.Concurrent_SFTP_appends_do_not_overwrite_each_other |
| B23 | fixed | 2ee7489, 96f6cd3, f895d80, a04215a, 75a8725, 4a7f2d7, 4a6c909 | NeedsReviewProviderLiveTests.A_GCS_copy_pinned_to_an_old_ETag_is_refused_and_a_move_keeps_nothing_behind; NeedsReviewProviderLiveTests.An_Azure_copy_pinned_to_an_old_ETag_is_refused_and_a_move_keeps_nothing_behind; NeedsReviewProviderLiveTests.An_S3_copy_pinned_to_an_old_ETag_is_refused_and_a_move_deletes_only_what_it_copied; NeedsReviewProviderS3Tests.A_destination_condition_is_sent_as_If_Match_or_refused_where_the_server_ignores_it; NeedsReviewProviderS3Tests.Copy_honours_the_expected_source_ETag_and_version; NeedsReviewProviderTests.A_local_copy_or_move_honours_the_expected_source_ETag_and_refuses_versions<br>*honoured or Unsupported on every backend; Local checks ExpectedSourceETag just before (documented)* |
| B24 | fixed | 3b8bf58, 4884e16 | NeedsReviewTransferApiTests.A_write_the_destination_stopped_carries_the_storage_error; NeedsReviewTransferTests.Disposing_a_committed_write_synchronously_releases_its_hold_on_the_callers_token; NeedsReviewTransferTests.Disposing_during_a_commit_lets_the_commit_finish |
| B25 | fixed | 3d0825a, 12c0f17 | NeedsReviewTransferTests.A_connections_copy_throws_on_a_cancel_like_every_other_connection_call; NeedsReviewTransferTests.Copy_and_move_report_a_cancel_and_an_unknown_connection_instead_of_throwing |
| B26 | fixed | 12c0f17 | NeedsReviewTransferTests.A_move_that_committed_but_kept_its_source_is_announced |
| B27 | fixed | cc846bc | NeedsReviewQueueTests.A_queued_job_claimed_elsewhere_leaves_the_queue_idle_and_runs_when_the_lease_lapses |
| B28 | fixed | cc846bc | NeedsReviewQueueTests.A_claim_refused_on_an_unchanged_record_backs_off |
| B29 | fixed | cc846bc | NeedsReviewQueueTests.A_failed_final_save_is_tried_again_while_the_lease_holds |
| B30 | fixed | cc846bc | NeedsReviewQueueTests.A_store_that_commits_and_then_throws_does_not_interrupt_the_job |
| B31 | fixed | cc846bc | NeedsReviewQueueTests.Finished_jobs_are_pruned_after_a_cancel_and_on_load |
| B32 | fixed | cc846bc | NeedsReviewQueueTests.A_stale_refresh_does_not_bring_back_a_removed_job; NeedsReviewQueueTests.Refresh_forgets_jobs_removed_by_another_process |
| B33 | fixed | cc846bc | NeedsReviewQueueTests.Enqueue_does_not_replace_a_newer_copy_adopted_meanwhile |
| B34 | fixed | cc846bc | NeedsReviewQueueTests.A_cancelled_enqueue_that_the_store_committed_returns_the_job; NeedsReviewQueueTests.A_store_read_failure_on_enqueue_is_reported_as_unavailable |
| B35 | fixed | cc846bc | NeedsReviewQueueTests.A_throwing_event_context_does_not_break_the_queue |
| B36 | fixed | cc846bc | NeedsReviewQueueTests.An_attempt_that_outlives_the_shutdown_timeout_still_gives_its_job_back; NeedsReviewQueueTests.Control_calls_after_dispose_do_not_write_to_the_store; NeedsReviewQueueTests.WaitForIdle_returns_when_the_queue_is_disposed |
| B37 | fixed | cc846bc | NeedsReviewQueueTests.An_invalid_refresh_interval_is_rejected |
| B38 | fixed | cc846bc | NeedsReviewQueueTests.Moving_a_job_among_equal_orders_changes_the_order; NeedsReviewQueueTests.Orders_stay_unique_across_queues_sharing_a_store |
| B39 | fixed | cc846bc | NeedsReviewQueueTests.A_job_store_failure_does_not_use_up_retries |
| B40 | fixed | cc846bc | NeedsReviewQueueTests.Enqueue_rejects_connections_and_options_that_do_not_apply_to_the_kind; NeedsReviewQueueTests.Same_work_ignores_default_options_and_connection_id_case |
| B41 | fixed | cc846bc, 2e72849 (docs) | NeedsReviewQueueTests.A_lease_can_be_released_without_a_save; NeedsReviewQueueTests.Fencing_tokens_do_not_restart_when_a_job_id_is_added_again; NeedsReviewQueueTests.Records_from_a_newer_schema_are_left_alone; NeedsReviewQueueTests.Records_round_trip_as_json_and_newer_ones_are_refused<br>*contract written in XML docs and transfers.md* |
| B42 | fixed | cc846bc | NeedsReviewQueueTests.The_last_progress_of_a_copy_always_arrives_even_when_it_fails |
| B43 | fixed | cc846bc | NeedsReviewQueueTests.Stopping_the_library_stops_its_queues_without_failing_running_jobs<br>*StorageLibrary disposes the queues it opened* |
| B44 | fixed | 522fa83 | NeedsReviewSyncTests.A_provider_that_throws_fails_its_step_and_the_run_keeps_its_report_and_baseline; NeedsReviewSyncTests.A_state_store_that_throws_gives_a_failure_or_a_report_with_the_reason_not_an_exception |
| B45 | fixed | 522fa83 | NeedsReviewSyncTests.A_deletion_made_while_a_path_was_excluded_is_carried_over_when_the_filter_is_lifted; NeedsReviewSyncTests.A_path_under_a_file_folder_clash_keeps_its_baseline_entry |
| B46 | fixed | 522fa83 | NeedsReviewSyncTests.A_destination_changed_right_before_the_promote_is_not_overwritten |
| B47 | fixed | 522fa83 | NeedsReviewSyncTests.A_planned_delete_leaves_a_path_whose_type_changed_after_the_plan |
| B48 | fixed | 522fa83 | NeedsReviewSyncTests.A_plan_applies_only_with_its_options_to_its_connections_and_in_its_format |
| B49 | fixed | 522fa83 | NeedsReviewSyncTests.Deletion_safety_rejects_NaN_counts_files_and_never_prints_a_share_equal_to_the_limit |
| B50 | fixed | 522fa83 | NeedsReviewSyncTests.A_folder_kept_by_excluded_items_is_not_recreated_on_the_side_that_deleted_it |
| B51 | fixed | 522fa83 | NeedsReviewSyncTests.A_missing_destination_folder_is_created_before_any_copy |
| B52 | fixed | 522fa83 | NeedsReviewSyncTests.A_blocked_sync_names_its_conflicts_and_a_newer_wins_tie_does_not_block_the_rest |
| B53 | fixed | 522fa83 | NeedsReviewSyncTests.A_source_without_an_ETag_that_changed_while_it_streamed_is_stale |
| B54 | fixed | 522fa83 | NeedsReviewSyncTests.Verify_records_the_content_digest_in_the_baseline; NeedsReviewSyncTests.Verify_reports_a_copy_replaced_right_after_its_promote |
| B55 | fixed | 522fa83 | NeedsReviewSyncTests.A_step_ended_by_the_stop_is_not_run_not_stale_and_the_apply_lock_is_released<br>*NotRun and Gates pruning fixed; documented: the apply lock is per process, Plan.Agreed lists every file* |
| B56 | fixed | 522fa83 | NeedsReviewCompareTests.A_file_of_unknown_size_does_not_pass_a_byte_budget; NeedsReviewCompareTests.The_hashing_budget_is_taken_for_both_sides_at_once_or_not_at_all; NeedsReviewSyncTests.Content_that_could_not_be_compared_is_left_alone_while_size_and_time_agree |
| B57 | fixed | 522fa83 | NeedsReviewCompareTests.A_connection_failure_while_hashing_still_fails_the_comparison; NeedsReviewCompareTests.One_unreadable_file_leaves_its_pair_undecided_and_the_comparison_goes_on |
| B58 | fixed | 522fa83, 4a6c909 | NeedsReviewCompareTests.A_link_target_resolves_inside_the_connection_only; NeedsReviewCompareTests.Follow_compares_what_a_link_points_to_when_the_provider_reports_the_link_itself; NeedsReviewCompareTests.What_a_provider_lists_below_a_skipped_link_to_a_folder_is_left_out_too; NeedsReviewProviderTests.A_recursive_listing_does_not_descend_into_a_directory_link |
| B59 | fixed | 8e99d97 | NeedsReviewWatchTests.A_failed_first_listing_does_not_report_everything_as_created; NeedsReviewWatchTests.A_folder_vanishing_part_way_through_a_poll_does_not_report_mass_deletes; NeedsReviewWatchTests.Native_watching_that_cannot_start_falls_back_to_polling<br>*new StorageWatchOptions.PollFailed* |
| B60 | fixed | 522fa83, 2ee7489 | NeedsReviewCompareTests.A_side_keeping_only_SHA256_is_not_downloaded; NeedsReviewCompareTests.Sides_keeping_different_checksums_download_only_one_side; NeedsReviewProviderS3Tests.An_SSE_C_object_reports_no_MD5 |
| B61 | fixed | 522fa83 | NeedsReviewCompareTests.A_kept_time_without_an_offset_is_UTC_and_a_kept_time_ahead_of_the_server_is_still_used<br>*kept time without offset is UTC and always used* |
| B62 | fixed | 75a8725 | NeedsReviewProviderLiveTests.FTP_hidden_names_are_found_without_listing_the_folder |
| B63 | partly fixed + documented | 4a6c909, 2e72849 | NeedsReviewProviderTests.The_local_ETag_is_a_weak_validator_of_write_time_creation_time_and_length<br>*weak ETag W/"mtime-ctime-length"; file id and change time are not exposed by .NET without a handle per file, so coarse-clock collisions remain and are documented* |
| B64 | fixed | 2ee7489 | NeedsReviewProviderLiveTests.The_S3_condition_probe_matches_what_the_server_does; NeedsReviewProviderS3Tests.A_connection_marked_not_enforcing_checks_conditions_itself_and_claims_no_atomic_conditions; NeedsReviewProviderS3Tests.Copy_and_delete_enforcement_is_probed_once_per_connection_and_cleans_up<br>*S3ConnectionConfig.ConditionalRequests (Auto probes copy/delete; uploads trusted)* |
| B65 | fixed + documented | 686a556 | NeedsReviewProviderTlsTests.A_PFX_without_a_private_key_is_refused<br>*PFX without key refused; container left by a crash documented* |
| B66 | fixed + documented | 686a556 | NeedsReviewProviderTlsTests.A_new_session_is_reported_only_to_the_caller_that_opened_it; NeedsReviewProviderTlsTests.A_resource_acquired_again_after_its_linger_ran_out_is_fresh_and_released_cleanly; NeedsReviewProviderTlsTests.A_shared_pool_takes_a_copy_of_the_settings; NeedsReviewProviderTlsTests.Racing_acquires_against_expiring_lingers_never_throw_or_leak<br>*flush being process-wide and the shared TLS recorder documented* |
| B67 | fixed | 4a6c909 | NeedsReviewProviderTests.Case_sensitivity_is_decided_by_the_root<br>*probed per root; subfolders assumed like the root (documented)* |
| B68 | fixed + documented | 522fa83, 32cc786 | NeedsReviewCompareTests.A_file_named_like_a_folder_does_not_hide_the_folder<br>*per-page sorting and possible repeated folders documented* |
| B69 | fixed | 4a7f2d7, 522fa83 | NeedsReviewCompareTests.A_folder_listed_in_the_servers_spelling_is_compared_not_dropped; NeedsReviewCompareTests.An_item_listed_outside_the_folder_fails_the_comparison_instead_of_vanishing; NeedsReviewProviderWebDavTests.An_item_in_the_servers_spelling_is_found_when_the_server_confirms_the_requested_spelling; NeedsReviewProviderWebDavTests.Hrefs_in_the_servers_spelling_stay_under_the_requested_root |
| B70 | documented | 2e72849 | —<br>*CodeLogic.Core's Error has no type property, so the factory kind is not observable; classify by code (errors-events.md, CHANGELOG)* |
| B71 | fixed | 3d0825a, 3b8bf58, 12c0f17 | NeedsReviewTransferTests.Runtime_only_settings_are_copied_when_the_library_is_created |
| D1 | fixed | beb23f1 | NeedsReviewEnumTests.Transfer_states_keep_the_numbers_4_8_93_stored<br>*NeedsReviewEnumTests; acceptance R2-3; CHANGELOG/MIGRATION say it* |
| D2 | documented | 2e72849 | *= A29* |
| D3 | fixed + documented | 2ee7489, 2e72849 | *create-only copies are single-request again below 5 GiB (A10); CHANGELOG describes the per-server enforcement* |
| D4 | documented | 2e72849 | *MIGRATION 'Upgrading from 4.8.93': StorageTransferJob and StorageSyncReport shapes, AutomaticRetries 3, dispose leaves jobs queued, tokens keyed by settings, same size not complete, SyncId/applies, file-versus-folder rule* |
| D5 | documented | 2e72849 | *MaxFinishedJobs 1,000; MaxItems 1,000,000; ItemRetries; Blocked outside FailedJobs; partial_failure -> NeedsReconciliation; case collision fails CompareAsync; inferred folders in CHANGELOG, MIGRATION, transfers.md* |
| D6 | documented | 2e72849 | *one CHANGELOG section against 4.8.93; types 4.8.93 never shipped are under Added (checked by reflection against the 4.8.93 package)* |
| D7 | documented | 2e72849 | *released in the 4.8 line by the owner's decision; CHANGELOG and MIGRATION say to rebuild dependants and pin the version; version.txt not changed* |
| D8 | fixed | 2e72849 | *Result<HealthStatus>; all 49 C# samples in README and docs/libs/storage compile against the branch* |
| D9 | documented | 2e72849 | *Atomic claims are now per server (AWS enforces, MinIO not on CopyObject/DeleteObject)* |
| D10 | documented | 2e72849 | *connections.md and README: listings over 250,000 items are not kept* |
| D11 | fixed + documented | 686a556, 2e72849 | *= A28* |

## Section C (cheap ones done)

- Transfers: `BackupRestored` for a single file; delete-marker as latest with a pinned read; a directory that
  wrote nothing reports `DestinationCommitted = false`; `ToResult()` without `Error`; staging stream leak when
  hashing throws; relay prefers the source's error; wrong comment; skipped links counted; `OpenWriteAsync`
  progress names the destination; native report takes bytes/ETag from the destination and enforcement from
  the connection. **Not done** (each is a behaviour decision or larger than cheap): `Recreate` ignoring the
  conflict policy; overlapping roots on two connections; `EnsureDirectoryAsync` claiming a folder created
  concurrently by someone else; resumed bytes in progress; `OpenWriteAsync` with `Skip`.
- Queue: pausing a paused job succeeds; `ClearAsync(null)` and a token overload; `StorageTransferInterruptedEvent`;
  `Retry-After` capped at 30 days and long waits stepped; outcome events only from this worker's saved record;
  `stored!` removed; Interrupted doc covers `RequeueInterruptedWhenSafe = false`. **Not done:** Pump,
  UpdateIdle, and Prune still scan every job under the lock.
- Sync: `RelativePath` doc; dead `|| Conflict`; withhold wording; `BaselineError`; `a.tar.gz` conflict names;
  quadratic folder-delete scans. **Documented:** retries restart large files from zero.
- Compare/watch: native channel bounded (8,192, then `Overflow`); one filesystem call per event; renames to
  or from internal names; indexed stale-child removal; NFC/NFD unified; 1 s regex timeout. **Documented:**
  FTP same-minute and deep incremental misses, one collision failing the sync, case folding, a `HEAD` per
  size-equal file.
- Providers: `ServerIdentityRecorder.Set` atomic; `Acquire` retires a holder of another type; a `cl1:` token
  on a flat listing is refused; `InitiateMultipartUpload` not cancelled half-way; non-seekable upload
  failures enriched. The settings hash is never logged (checked).

## Section E

- Tests that passed whatever the code did are rewritten (pinned move, condition refused, resume tail byte
  count, tampered token, store that throws, same worker id, concurrency limit, priorities, history cap,
  adaptive halving, the retried reconciliation job, timing in `TransferQueueTests`, `StorageHubNeedsPhase1/5`,
  `ReviewCancellationTests`), each tagged `// needs-review E` where changed.
- Live tests that returned early now Skip (`[SftpAndS3Fact]`).
- The case-spelling sync tests run on every OS (an in-memory case-sensitive store).
- Missing tests added per area (see the `E` and per-finding tags). FTP mutual TLS through a shared pool is
  unit-tested only: the compose file has no FTP server that requires a client certificate.
- CI: `storage-windows-tls` runs the mutual-TLS tests on Windows (SChannel, key containers) against rclone's
  WebDAV server; the Caddy image is pinned to 2.11.4.
